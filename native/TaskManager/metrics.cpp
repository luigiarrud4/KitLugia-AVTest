// metrics.cpp — coleta nativa de metricas (processos, sistema, servicos, startup, usuarios)
// Fontes: Toolhelp32, GetProcessTimes, K32GetProcessMemoryInfo, GetProcessIoCounters,
//         NtQueryInformationProcess (handles/prioridade), PDH (GPU/rede/disco/CPU),
//         WTS (sessoes), SCM (servicos), Registry Run keys (startup), TCP EStats (rede/PID).
#include "tm.h"
#include <psapi.h>
#include <wtsapi32.h>
#include <userenv.h>
#include <winternl.h>
#include <netioapi.h>
#include <iphlpapi.h>
#include <tcpestats.h>
#include <pdhmsg.h>
#include <sddl.h>
#include <tlhelp32.h>

#pragma comment(lib, "wtsapi32.lib")
#pragma comment(lib, "userenv.lib")
#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "ntdll.lib")

// ---------------------------------------------------------------- estado PDH
struct PdhCtx {
    PDH_HQUERY qCpu=nullptr;   PDH_HCOUNTER cCpu=nullptr;
    PDH_HQUERY qMem=nullptr;   PDH_HCOUNTER cMem=nullptr, cMemLim=nullptr;
    PDH_HQUERY qDsk=nullptr;   PDH_HCOUNTER cDskTime=nullptr, cDskQueue=nullptr, cDskLat=nullptr,
                               cDskRd=nullptr, cDskWr=nullptr;
    PDH_HQUERY qNet=nullptr;   PDH_HCOUNTER cNet=nullptr;
    PDH_HQUERY qGpu=nullptr;   PDH_HCOUNTER cGpu=nullptr;
    bool ok=false;
};
static PdhCtx g_pdh;

static bool PdhAdd(PDH_HQUERY q, const wchar_t* path, PDH_HCOUNTER* c)
{
    return PdhAddEnglishCounterW(q, path, 0, c) == ERROR_SUCCESS;
}
static double PdhVal(PDH_HCOUNTER c)
{
    PDH_FMT_COUNTERVALUE v{};
    if (PdhGetFormattedCounterValue(c, PDH_FMT_DOUBLE, nullptr, &v) != ERROR_SUCCESS) return 0;
    return v.doubleValue;
}

void MetricsInit()
{
    SYSTEM_INFO si; GetSystemInfo(&si);
    g.sys.logicalCores = (int)si.dwNumberOfProcessors;
    // CPU total
    if (PdhOpenQueryW(nullptr,0,&g_pdh.qCpu)==ERROR_SUCCESS)
        PdhAdd(g_pdh.qCpu, L"\\Processor(_Total)\\% Processor Time", &g_pdh.cCpu);
    // Memoria: % committed + limite
    if (PdhOpenQueryW(nullptr,0,&g_pdh.qMem)==ERROR_SUCCESS) {
        PdhAdd(g_pdh.qMem, L"\\Memory\\% Committed Bytes In Use", &g_pdh.cMem);
        PdhAdd(g_pdh.qMem, L"\\Memory\\Commit Limit", &g_pdh.cMemLim);
    }
    // Disco fisico total
    if (PdhOpenQueryW(nullptr,0,&g_pdh.qDsk)==ERROR_SUCCESS) {
        PdhAdd(g_pdh.qDsk, L"\\PhysicalDisk(_Total)\\% Disk Time", &g_pdh.cDskTime);
        PdhAdd(g_pdh.qDsk, L"\\PhysicalDisk(_Total)\\Avg. Disk Queue Length", &g_pdh.cDskQueue);
        PdhAdd(g_pdh.qDsk, L"\\PhysicalDisk(_Total)\\Avg. Disk sec/Transfer", &g_pdh.cDskLat);
        PdhAdd(g_pdh.qDsk, L"\\PhysicalDisk(_Total)\\Disk Read Bytes/sec", &g_pdh.cDskRd);
        PdhAdd(g_pdh.qDsk, L"\\PhysicalDisk(_Total)\\Disk Write Bytes/sec", &g_pdh.cDskWr);
    }
    // Rede: soma de todas as interfaces
    if (PdhOpenQueryW(nullptr,0,&g_pdh.qNet)==ERROR_SUCCESS)
        PdhAdd(g_pdh.qNet, L"\\Network Interface(*)\\Bytes Total/sec", &g_pdh.cNet);
    // GPU: soma de todas as engines
    if (PdhOpenQueryW(nullptr,0,&g_pdh.qGpu)==ERROR_SUCCESS)
        PdhAdd(g_pdh.qGpu, L"\\GPU Engine(*)\\Utilization Percentage", &g_pdh.cGpu);
    g_pdh.ok = true;
}

// ---------------------------------------------------------------- helpers Win32
static std::wstring BaseName(const std::wstring& p)
{
    size_t i = p.find_last_of(L"\\/");
    return i==std::wstring::npos ? p : p.substr(i+1);
}
static double FTtoSec(const FILETIME& a, const FILETIME& b)
{
    ULARGE_INTEGER x,y;
    x.LowPart=a.dwLowDateTime; x.HighPart=a.dwHighDateTime;
    y.LowPart=b.dwLowDateTime; y.HighPart=b.dwHighDateTime;
    return (double)(y.QuadPart - x.QuadPart) / 1e7;
}
static double NowSecFT(const FILETIME& ft)
{
    ULARGE_INTEGER u; u.LowPart=ft.dwLowDateTime; u.HighPart=ft.dwHighDateTime;
    return (double)u.QuadPart / 1e7;
}

// NtQueryInformationProcess para handle count
struct NT_PROC_BASIC_INFO { NTSTATUS ExitStatus; PVOID PebBaseAddress; ULONG_PTR AffinityMask;
    KPRIORITY BasePriority; ULONG_PTR UniqueProcessId; ULONG_PTR InheritedFromUniqueProcessId; };
typedef NTSTATUS (NTAPI *PFN_NtQIP)(HANDLE, ULONG, PVOID, ULONG, PULONG);
static PFN_NtQIP g_NtQIP = nullptr;

static int QueryHandleCount(HANDLE h)
{
    if (!g_NtQIP) {
        HMODULE n = GetModuleHandleW(L"ntdll.dll");
        if (n) g_NtQIP = (PFN_NtQIP)GetProcAddress(n, "NtQueryInformationProcess");
    }
    if (!g_NtQIP) return 0;
    ULONG cnt=0; ULONG ret=0;
    // ProcessHandleCount = 20
    NTSTATUS st = g_NtQIP(h, 20, &cnt, sizeof(cnt), &ret);
    return st==0 ? (int)cnt : 0;
}

static std::wstring QueryUserName(HANDLE hProc)
{
    static std::unordered_map<DWORD,std::wstring> cache;
    DWORD pid = GetProcessId(hProc);
    auto it = cache.find(pid);
    if (it != cache.end()) return it->second;
    std::wstring out = L"—";
    HANDLE tok=nullptr;
    if (OpenProcessToken(hProc, TOKEN_QUERY, &tok)) {
        DWORD need=0;
        GetTokenInformation(tok, TokenUser, nullptr, 0, &need);
        if (need) {
            std::vector<BYTE> buf(need);
            if (GetTokenInformation(tok, TokenUser, buf.data(), need, &need)) {
                auto* tu = (TOKEN_USER*)buf.data();
                wchar_t name[128]={0}, dom[64]={0}; DWORD nl=128, dl=64; SID_NAME_USE use;
                if (LookupAccountSidW(nullptr, tu->User.Sid, name, &nl, dom, &dl, &use))
                    out = name;
                else {
                    WCHAR* sidStr=nullptr;
                    if (ConvertSidToStringSidW(tu->User.Sid, &sidStr)) {
                        out = sidStr; LocalFree(sidStr);
                    }
                }
            }
        }
        CloseHandle(tok);
    }
    cache[pid]=out;
    return out;
}

// ---------------------------------------------------------------- coleta de processos
static std::unordered_map<DWORD,int> g_threadCount;
static std::unordered_map<DWORD,ULONGLONG> g_prevNetPid;   // bytes acumulados TCP por pid
static std::unordered_map<std::wstring,ULONGLONG> g_prevNetConn; // por conexao p/ delta real

struct NetConnKey {
    DWORD local, remote; USHORT lp, rp;
    bool operator==(const NetConnKey& o) const { return local==o.local&&remote==o.remote&&lp==o.lp&&rp==o.rp; }
};
struct NetConnKeyHash { size_t operator()(const NetConnKey& k) const {
    return std::hash<DWORD>()(k.local) ^ (std::hash<DWORD>()(k.remote)<<1) ^ (std::hash<int>()(k.lp)<<7) ^ (std::hash<int>()(k.rp)<<13);
}};

// Rede por PID via TCP EStats (conexoes TCP; UDP fica de fora na v1)
static void CollectNetPerPid(std::unordered_map<DWORD,double>& outBps)
{
    outBps.clear();
    // le a tabela TCP com PID dono
    ULONG sz=0;
    GetExtendedTcpTable(nullptr, &sz, FALSE, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
    if (!sz) return;
    std::vector<BYTE> buf(sz);
    if (GetExtendedTcpTable(buf.data(), &sz, FALSE, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != NO_ERROR) return;
    auto* tbl = (MIB_TCPTABLE_OWNER_PID*)buf.data();
    static std::unordered_map<NetConnKey,ULONGLONG,NetConnKeyHash> prevIn, prevOut;
    for (DWORD i=0;i<tbl->dwNumEntries;i++) {
        auto& r = tbl->table[i];
        NetConnKey k{ r.dwLocalAddr, r.dwRemoteAddr, (USHORT)r.dwLocalPort, (USHORT)r.dwRemotePort };
        TCP_ESTATS_DATA_ROD_v0 rod{};
        // assinatura real: (PMIB_TCPROW, TCP_ESTATS_TYPE, Rw,RwVer,RwSize, Ros,RosVer,RosSize, Rod,RodVer,RodSize)
        if (GetPerTcpConnectionEStats((PMIB_TCPROW)&r, TcpConnectionEstatsData,
                nullptr,0,0, nullptr,0,0, (PUCHAR)&rod, 0, sizeof(rod)) != NO_ERROR) continue;
        ULONGLONG in = rod.DataBytesIn, out = rod.DataBytesOut;
        ULONGLONG pi=0, po=0;
        auto ii=prevIn.find(k), io=prevOut.find(k);
        if (ii!=prevIn.end()) pi=ii->second; if (io!=prevOut.end()) po=io->second;
        prevIn[k]=in; prevOut[k]=out;
        double d = (double)((in>pi?in-pi:0) + (out>po?out-po:0));
        outBps[r.dwOwningPid] += d;   // bytes desde a ultima coleta (~1s)
    }
}

// ---------------------------------------------------------------- PDH array p/ GPU por PID
struct GpuInst { DWORD pid; double pct; };
static std::vector<GpuInst> CollectGpuPerPid(double& totalPct)
{
    std::vector<GpuInst> res;
    totalPct = 0;
    if (!g_pdh.qGpu) return res;
    if (PdhCollectQueryData(g_pdh.qGpu)!=ERROR_SUCCESS) return res;
    DWORD n=0,cnt=0;
    PDH_STATUS s = PdhGetFormattedCounterArrayW(g_pdh.cGpu, PDH_FMT_DOUBLE, &n, &cnt, nullptr);
    if (s==PDH_MORE_DATA) {
        std::vector<BYTE> items(n);
        s = PdhGetFormattedCounterArrayW(g_pdh.cGpu, PDH_FMT_DOUBLE, &n, &cnt, (PDH_FMT_COUNTERVALUE_ITEM_W*)items.data());
        if (s==ERROR_SUCCESS) {
            auto* arr=(PDH_FMT_COUNTERVALUE_ITEM_W*)items.data();
            for (DWORD i=0;i<cnt;i++) {
                const wchar_t* in = arr[i].szName;
                // formato: "pid_1234_luid_0x..._engtype_3D"
                const wchar_t* p = wcsstr(in, L"pid_");
                if (!p) continue;
                DWORD pid = (DWORD)_wtoi(p+4);
                double v = arr[i].FmtValue.doubleValue;
                if (pid) { res.push_back({pid,v}); totalPct += v; }
            }
        }
    }
    if (totalPct>100) totalPct=100;
    return res;
}

// ---------------------------------------------------------------- coleta principal
static FILETIME g_prevIdleT{}, g_prevKT{}, g_prevUT{};
static bool g_haveSysT=false;

static ULONGLONG FTQ(const FILETIME& f){ ULARGE_INTEGER u; u.LowPart=f.dwLowDateTime; u.HighPart=f.dwHighDateTime; return u.QuadPart; }

void CollectSystemOnly()
{
    // CPU total: PDH e o primario; GetSystemTimes (delta) e o fallback.
    FILETIME cI{},cK{},cU{};
    if (GetSystemTimes(&cI,&cK,&cU)) {
        if (g_haveSysT) {
            double dI=(double)(FTQ(cI)-FTQ(g_prevIdleT));
            double dK=(double)(FTQ(cK)-FTQ(g_prevKT));
            double dU=(double)(FTQ(cU)-FTQ(g_prevUT));
            double tot=dK+dU;
            if (tot>0) g.sys.cpuPct = 100.0*(tot-dI)/tot;
        }
        g_prevIdleT=cI; g_prevKT=cK; g_prevUT=cU; g_haveSysT=true;
    }
    // Memoria via K32GetPerformanceInfo
    PERFORMANCE_INFORMATION pi{}; pi.cb=sizeof(pi);
    if (K32GetPerformanceInfo(&pi,sizeof(pi))) {
        double total=(double)pi.PhysicalTotal/1024.0/1024.0; // MB
        double avail=(double)pi.PhysicalAvailable/1024.0/1024.0;
        g.sys.memTotalGB = total/1024.0;
        g.sys.memUsedGB  = (total-avail)/1024.0;
        g.sys.memPct     = total>0 ? 100.0*(total-avail)/total : 0;
    }
    // PDH
    if (g_pdh.qCpu && PdhCollectQueryData(g_pdh.qCpu)==ERROR_SUCCESS)
        g.sys.cpuPct = PdhVal(g_pdh.cCpu); // PDH e mais estavel que delta manual
    if (g_pdh.qMem && PdhCollectQueryData(g_pdh.qMem)==ERROR_SUCCESS) {
        double m=PdhVal(g_pdh.cMem); if (m>0) g.sys.memPct=m;
    }
    if (g_pdh.qDsk && PdhCollectQueryData(g_pdh.qDsk)==ERROR_SUCCESS) {
        g.sys.diskActive  = PdhVal(g_pdh.cDskTime);
        g.sys.diskQueue   = PdhVal(g_pdh.cDskQueue);
        g.sys.diskLatencyMs = PdhVal(g_pdh.cDskLat)*1000.0;
        g.sys.diskReadMBps  = PdhVal(g_pdh.cDskRd)/1048576.0;
        g.sys.diskWriteMBps = PdhVal(g_pdh.cDskWr)/1048576.0;
    }
    if (g_pdh.qNet && PdhCollectQueryData(g_pdh.qNet)==ERROR_SUCCESS) {
        // array de interfaces: soma bytes/sec
        DWORD n=0,cnt=0;
        PDH_STATUS s = PdhGetFormattedCounterArrayW(g_pdh.cNet, PDH_FMT_DOUBLE, &n, &cnt, nullptr);
        if (s==PDH_MORE_DATA) {
            std::vector<BYTE> items(n);
            s = PdhGetFormattedCounterArrayW(g_pdh.cNet, PDH_FMT_DOUBLE, &n, &cnt, (PDH_FMT_COUNTERVALUE_ITEM_W*)items.data());
            if (s==ERROR_SUCCESS) {
                auto* arr=(PDH_FMT_COUNTERVALUE_ITEM_W*)items.data();
                double sum=0; for(DWORD i=0;i<cnt;i++) sum+=arr[i].FmtValue.doubleValue;
                g.sys.netSentBps=sum; g.sys.netRecvBps=sum; // Bytes Total = send+recv
            }
        }
    }
    double gpuTot=0;
    auto gpuInst = CollectGpuPerPid(gpuTot);
    g.sys.gpuPct = gpuTot;
    // frequencia/temperatura/potencia: nao implementado na v1 nativa (mostra "—")
    g.sys.freqMhz=0; g.sys.tempC=-1; g.sys.powerW=-1;
    // aplica GPU por PID no vetor de processos
    {
        std::unordered_map<DWORD,double> m;
        for (auto& gi: gpuInst) m[gi.pid]+=gi.pct;
        std::lock_guard<std::mutex> lk(g.dataMtx);
        for (auto& p: g.procs) p.gpuPct = m.count(p.pid)? m[p.pid] : 0.0;
    }
}

void CollectAll()
{
    if (g.collecting.exchange(true)) return;
    try {
        // ---- threads por pid (Toolhelp32 snapshot) ----
        g_threadCount.clear();
        HANDLE hThr = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);
        if (hThr!=INVALID_HANDLE_VALUE) {
            THREADENTRY32 te{sizeof(te)};
            if (Thread32First(hThr,&te)) do {
                g_threadCount[te.th32OwnerProcessID]++;
            } while (Thread32Next(hThr,&te));
            CloseHandle(hThr);
        }
        // ---- processos ----
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
        if (snap==INVALID_HANDLE_VALUE) { g.collecting=false; return; }
        // rede por pid (TCP)
        std::unordered_map<DWORD,double> netBps;
        CollectNetPerPid(netBps);

        std::vector<ProcRow> rows;
        rows.reserve(512);
        PROCESSENTRY32W pe{sizeof(pe)};
        if (Process32FirstW(snap,&pe)) do {
            ProcRow r; r.pid=pe.th32ProcessID; r.ppid=pe.th32ParentProcessID;
            r.name=pe.szExeFile;
            ProcessIdToSessionId(r.pid,&r.sessionId);
            r.threads = g_threadCount.count(r.pid)? g_threadCount[r.pid] : 0;
            // abrir com direitos decrescentes (paridade TMOG: QUERY_LIMITED primeiro)
            HANDLE hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|PROCESS_VM_READ, FALSE, r.pid);
            if (!hp) hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, r.pid);
            if (!hp) hp = OpenProcess(PROCESS_QUERY_INFORMATION, FALSE, r.pid);
            if (hp) {
                // path
                wchar_t path[512]={0}; DWORD psz=512;
                if (QueryFullProcessImageNameW(hp,0,path,&psz)) r.path=path;
                // tempo -> cpu%
                FILETIME c,e,k,u;
                if (GetProcessTimes(hp,&c,&e,&k,&u)) {
                    ULARGE_INTEGER kk,uu;
                    kk.LowPart=k.dwLowDateTime;kk.HighPart=k.dwHighDateTime;
                    uu.LowPart=u.dwLowDateTime;uu.HighPart=u.dwHighDateTime;
                    double procSec=(double)(kk.QuadPart+uu.QuadPart)/1e7;
                    // delta desde a ultima coleta: usamos media movel simples — armazena em prev via mapa estatico
                    static std::unordered_map<DWORD,std::pair<double,FILETIME>> prevCpu;
                    double cpuPct=0;
                    auto it=prevCpu.find(r.pid);
                    FILETIME nowFt; GetSystemTimeAsFileTime(&nowFt);
                    if (it!=prevCpu.end()) {
                        double dSec = FTtoSec(it->second.second, nowFt);
                        double dProc = procSec - it->second.first;
                        if (dSec>0 && dProc>=0) {
                            int cores = g.sys.logicalCores>0? g.sys.logicalCores:1;
                            cpuPct = (dProc/dSec)/cores*100.0; // fracao de UM nucleo -> % da capacidade? ajustado abaixo
                            // TMOG/Kit mostram % do total: (procDelta/sysDeltaTotal)*100
                            // aqui usamos aproximacao por tempo decorrido e nucleos
                        }
                    }
                    prevCpu[r.pid]={procSec,nowFt};
                    r.cpuPct=cpuPct;
                }
                // memoria
                PROCESS_MEMORY_COUNTERS_EX mc{}; mc.cb=sizeof(mc);
                if (K32GetProcessMemoryInfo(hp,(PPROCESS_MEMORY_COUNTERS)&mc,sizeof(mc))) {
                    r.memMB   = (double)mc.WorkingSetSize/1048576.0;
                    r.peakMB  = (double)mc.PeakWorkingSetSize/1048576.0;
                    r.privMB  = (double)mc.PrivateUsage/1048576.0;
                }
                // IO disco (delta read/write por pid)
                IO_COUNTERS ioc{};
                if (GetProcessIoCounters(hp,&ioc)) {
                    FILETIME n2; GetSystemTimeAsFileTime(&n2);
                    ULONGLONG rd=ioc.ReadTransferCount, wr=ioc.WriteTransferCount;
                    static std::unordered_map<DWORD,std::pair<std::pair<ULONGLONG,ULONGLONG>,FILETIME>> prevIo2;
                    auto it3=prevIo2.find(r.pid);
                    if (it3!=prevIo2.end()) {
                        double ds=FTtoSec(it3->second.second,n2); if(ds<=0)ds=1;
                        r.diskReadBps =(double)(rd>it3->second.first.first? rd-it3->second.first.first:0)/ds;
                        r.diskWriteBps=(double)(wr>it3->second.first.second? wr-it3->second.first.second:0)/ds;
                    }
                    prevIo2[r.pid]={ {rd,wr}, n2 };
                    r.ioRead=rd; r.ioWrite=wr;
                }
                // handles
                r.handles = QueryHandleCount(hp);
                // prioridade
                DWORD pr = GetPriorityClass(hp);
                if (pr) r.priority = PrioName(pr);
                // usuario
                r.user = QueryUserName(hp);
                CloseHandle(hp);
            }
            // rede (acumulada por pid nesta coleta = bytes no ultimo ~1s)
            auto ni = netBps.find(r.pid);
            if (ni!=netBps.end()) r.netBps = ni->second;
            rows.push_back(std::move(r));
        } while (Process32NextW(snap,&pe));
        CloseHandle(snap);
        {
            std::lock_guard<std::mutex> lk(g.dataMtx);
            g.procs = std::move(rows);
        }
        CollectSystemOnly();
    } catch (...) {}
    g.collecting=false;
}

// ---------------------------------------------------------------- servicos
void CollectServices()
{
    std::vector<SvcRow> out;
    SC_HANDLE scm = OpenSCManagerW(nullptr,nullptr,SC_MANAGER_ENUMERATE_SERVICE);
    if (!scm) return;
    DWORD need=0,cnt=0,resume=0;
    EnumServicesStatusExW(scm,SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
        nullptr,0,&need,&cnt,&resume,nullptr);
    if (need) {
        std::vector<BYTE> buf(need);
        if (EnumServicesStatusExW(scm,SC_ENUM_PROCESS_INFO,SERVICE_WIN32,SERVICE_STATE_ALL,
                buf.data(),need,&need,&cnt,&resume,nullptr)) {
            auto* arr=(ENUM_SERVICE_STATUS_PROCESSW*)buf.data();
            for (DWORD i=0;i<cnt;i++) {
                SvcRow s;
                s.name=arr[i].lpServiceName;
                s.display=arr[i].lpDisplayName?arr[i].lpDisplayName:L"";
                s.pid=arr[i].ServiceStatusProcess.dwProcessId;
                s.state=arr[i].ServiceStatusProcess.dwCurrentState;
                out.push_back(std::move(s));
            }
        }
    }
    CloseServiceHandle(scm);
    std::lock_guard<std::mutex> lk(g.dataMtx);
    g.svcs=std::move(out);
}

// ---------------------------------------------------------------- startup
static void ReadRunKey(HKEY root, const wchar_t* sub, const wchar_t* loc, std::vector<StartRow>& out)
{
    HKEY k=nullptr;
    if (RegOpenKeyExW(root,sub,0,KEY_READ,&k)!=ERROR_SUCCESS) return;
    wchar_t name[256]; DWORD ni=256; DWORD type=0; BYTE data[2048]; DWORD sz=sizeof(data);
    for (DWORD idx=0; ;idx++) {
        ni=256; sz=sizeof(data); memset(data,0,sizeof(data));
        LONG rc = RegEnumValueW(k,idx,name,&ni,nullptr,&type,data,&sz);
        if (rc!=ERROR_SUCCESS) break;
        if (type==REG_SZ || type==REG_EXPAND_SZ) {
            StartRow r; r.name=name;
            r.cmd=(wchar_t*)data; if (r.cmd.size()>300) r.cmd.resize(300);
            r.location=loc;
            out.push_back(std::move(r));
        }
    }
    RegCloseKey(k);
}
static bool StartupEnabled(const wchar_t* name)
{
    // StartupApproved\Run: valor binario 02/06 = habilitado, 03 = desabilitado
    HKEY k=nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER,L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run",0,KEY_READ,&k)!=ERROR_SUCCESS) return true;
    BYTE b[16]={0}; DWORD sz=sizeof(b); DWORD t=0;
    bool en=true;
    if (RegQueryValueExW(k,name,nullptr,&t,b,&sz)==ERROR_SUCCESS && sz>=2) en=(b[0]!=3);
    RegCloseKey(k);
    return en;
}
void CollectStartup()
{
    std::vector<StartRow> out;
    ReadRunKey(HKEY_CURRENT_USER,L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run",L"HKCU\\...\\Run",out);
    ReadRunKey(HKEY_LOCAL_MACHINE,L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run",L"HKLM\\...\\Run",out);
    ReadRunKey(HKEY_CURRENT_USER,L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce",L"HKCU\\...\\RunOnce",out);
    ReadRunKey(HKEY_LOCAL_MACHINE,L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce",L"HKLM\\...\\RunOnce",out);
    for (auto& r: out) r.enabled = StartupEnabled(r.name.c_str());
    std::lock_guard<std::mutex> lk(g.dataMtx);
    g.starts=std::move(out);
}

// ---------------------------------------------------------------- usuarios
void CollectUsers()
{
    std::vector<UserRow> out;
    // agrega processos por sessionId
    std::unordered_map<DWORD,UserRow> bySess;
    {
        std::lock_guard<std::mutex> lk(g.dataMtx);
        for (auto& p: g.procs) {
            auto& u = bySess[p.sessionId];
            u.session = (int)p.sessionId;
            u.procCount++;
            u.cpu += p.cpuPct;
            u.memMB += p.memMB;
            u.pids.push_back(p.pid);
        }
    }
    // nomes via WTS
    PWTS_SESSION_INFO si=nullptr; DWORD n=0;
    if (WTSEnumerateSessionsW(WTS_CURRENT_SERVER,0,1,&si,&n)) {
        for (DWORD i=0;i<n;i++) {
            if (si[i].State==WTSDisconnected) continue;
            DWORD sid = si[i].SessionId;
            wchar_t* uname=nullptr;
            DWORD ul=0;
            if (WTSQuerySessionInformationW(WTS_CURRENT_SERVER,sid,WTSUserName,&uname,&ul)) {
                std::wstring un = uname?uname:L"";
                WTSFreeMemory(uname);
                if (!un.empty() && un!=L"Console" && un!=L"") {
                    auto it=bySess.find(sid);
                    UserRow u;
                    u.name=un; u.session=(int)sid;
                    if (it!=bySess.end()) { u.procCount=it->second.procCount; u.cpu=it->second.cpu; u.memMB=it->second.memMB; u.pids=it->second.pids; }
                    out.push_back(std::move(u));
                }
            }
        }
        WTSFreeMemory(si);
    }
    // sessao 0 (servicos) se houver processos
    auto it0=bySess.find(0);
    if (it0!=bySess.end() && it0->second.procCount>0) {
        UserRow u; u.name=L"SYSTEM/Servicos"; u.session=0;
        u.procCount=it0->second.procCount; u.cpu=it0->second.cpu; u.memMB=it0->second.memMB; u.pids=it0->second.pids;
        out.push_back(std::move(u));
    }
    std::sort(out.begin(),out.end(),[](const UserRow&a,const UserRow&b){return a.memMB>b.memMB;});
    std::lock_guard<std::mutex> lk(g.dataMtx);
    g.users=std::move(out);
}

// ---------------------------------------------------------------- formatacao
std::wstring FormatBytes(double b)
{
    wchar_t buf[64];
    if (b>=1073741824.0) swprintf(buf,64,L"%.1f GB",b/1073741824.0);
    else if (b>=1048576.0) swprintf(buf,64,L"%.0f MB",b/1048576.0);
    else if (b>=1024.0) swprintf(buf,64,L"%.0f KB",b/1024.0);
    else swprintf(buf,64,L"%.0f B",b);
    return buf;
}
std::wstring FormatBps(double b)
{
    wchar_t buf[64];
    if (b>=1048576.0) swprintf(buf,64,L"%.1f MB/s",b/1048576.0);
    else if (b>=1024.0) swprintf(buf,64,L"%.0f KB/s",b/1024.0);
    else swprintf(buf,64,L"%.0f B/s",b);
    return buf;
}
std::wstring FormatPct(double v)
{
    wchar_t buf[32]; swprintf(buf,32,L"%.1f%%",v); return buf;
}
const wchar_t* PrioName(DWORD p)
{
    switch(p){
        case REALTIME_PRIORITY_CLASS: return L"Realtime";
        case HIGH_PRIORITY_CLASS: return L"Alta";
        case ABOVE_NORMAL_PRIORITY_CLASS: return L"Acima do normal";
        case NORMAL_PRIORITY_CLASS: return L"Normal";
        case BELOW_NORMAL_PRIORITY_CLASS: return L"Abaixo do normal";
        case IDLE_PRIORITY_CLASS: return L"Ocioso";
        default: return L"—";
    }
}
