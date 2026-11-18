// main.cpp — janela Win32 + D3D11/D2D1/DWrite do Kit Task Manager nativo
// Mesma linguagem do GameOverlayTest: swapchain DXGI flip-model + D2D1 imediato.
#include "tm.h"

// ntdll para suspend/resume
static BOOL (NTAPI *g_NtSuspend)(HANDLE) = nullptr;
static BOOL (NTAPI *g_NtResume)(HANDLE) = nullptr;

static int kProcSortKeyOf(size_t c);

#define WM_APP_RENDER (WM_APP+1)

static void EnsureNtSusp()
{
    if (!g_NtSuspend) {
        HMODULE n=GetModuleHandleW(L"ntdll.dll");
        if(n){ g_NtSuspend=(decltype(g_NtSuspend))GetProcAddress(n,"NtSuspendProcess");
               g_NtResume =(decltype(g_NtResume)) GetProcAddress(n,"NtResumeProcess"); }
    }
}

// ---------------------------------------------------------------- D3D/D2D init
static bool CreateDevice()
{
    UINT flags=D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL fls[]={D3D_FEATURE_LEVEL_11_1,D3D_FEATURE_LEVEL_11_0};
    if (FAILED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,flags,fls,2,
            D3D11_SDK_VERSION,&g.d3d,nullptr,nullptr))) return false;
    IDXGIDevice* dxgiD=nullptr; g.d3d->QueryInterface(&dxgiD);
    IDXGIAdapter* ad=nullptr;   dxgiD->GetAdapter(&ad);
    IDXGIFactory2* fac=nullptr; ad->GetParent(__uuidof(IDXGIFactory2),(void**)&fac);

    DXGI_SWAP_CHAIN_DESC1 sd={};
    sd.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
    sd.SampleDesc.Count=1;
    sd.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.BufferCount=2;
    sd.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    sd.AlphaMode=DXGI_ALPHA_MODE_IGNORE;
    sd.Width=0; sd.Height=0;
    if (FAILED(fac->CreateSwapChainForHwnd(g.d3d,g.hwnd,&sd,nullptr,nullptr,&g.swap))) return false;

    D2D1_FACTORY_OPTIONS fo={};
    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,__uuidof(ID2D1Factory1),&fo,(void**)&g.d2f))) return false;
    if (FAILED(g.d2f->CreateDevice(dxgiD,&g.d2dev))) return false;
    if (FAILED(g.d2dev->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,&g.dc))) return false;
    if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,__uuidof(IDWriteFactory),(IUnknown**)&g.dw))) return false;

    dxgiD->Release();ad->Release();fac->Release();
    EnsureTargets();
    return true;
}

// ---------------------------------------------------------------- coleta (worker)
static void CollectWorker()
{
    static int tick=0; tick++;
    CollectAll();
    if (tick%3==1){ CollectServices(); CollectStartup(); CollectUsers(); }
    PostMessageW(g.hwnd,WM_APP_RENDER,0,0);
}
static void StartCollect(){
    if (g.collecting.load()) return;
    CreateThread(nullptr,0,[](LPVOID)->DWORD{ CollectWorker(); return 0; },nullptr,0,nullptr);
}

// ---------------------------------------------------------------- acoes
static HANDLE OpenSel(DWORD access)
{
    EnsureNtSusp();
    DWORD pid=g.selectedPid;
    if(!pid && !g.filtIdx.empty()){
        std::lock_guard<std::mutex> lk(g.dataMtx);
        if(!g.filtIdx.empty()) pid=g.procs[g.filtIdx[0]].pid;
    }
    if(!pid) return nullptr;
    return OpenProcess(access,FALSE,pid);
}
void KillSelected(){ HANDLE h=OpenSel(PROCESS_TERMINATE); if(h){TerminateProcess(h,1);CloseHandle(h);} }
void SuspendSelected(){ HANDLE h=OpenSel(PROCESS_SUSPEND_RESUME); if(h&&g_NtSuspend){g_NtSuspend(h);CloseHandle(h);} }
void ResumeSelected(){ HANDLE h=OpenSel(PROCESS_SUSPEND_RESUME); if(h&&g_NtResume){g_NtResume(h);CloseHandle(h);} }

static void CopyText(const std::wstring& s){
    if(!OpenClipboard(g.hwnd))return;
    EmptyClipboard();
    size_t bytes=(s.size()+1)*sizeof(wchar_t);
    HGLOBAL hg=GlobalAlloc(GMEM_MOVEABLE,bytes);
    if(hg){void*p=GlobalLock(hg);if(p){memcpy(p,s.c_str(),bytes);GlobalUnlock(hg);
        SetClipboardData(CF_UNICODETEXT,hg);}else GlobalFree(hg);}
    CloseClipboard();
}

// ---------------------------------------------------------------- menu contexto
void ShowContextMenu(int x,int y)
{
    HMENU m=CreatePopupMenu();
    AppendMenuW(m,MF_STRING,1,L"Finalizar tarefa");
    AppendMenuW(m,MF_STRING,2,L"Suspender");
    AppendMenuW(m,MF_STRING,3,L"Retomar");
    AppendMenuW(m,MF_SEPARATOR,0,nullptr);
    AppendMenuW(m,MF_STRING,4,L"Copiar PID");
    AppendMenuW(m,MF_STRING,5,L"Copiar nome");
    int cmd=TrackPopupMenu(m,TPM_RETURNCMD|TPM_NONOTIFY,x,y,0,g.hwnd,nullptr);
    DestroyMenu(m);
    if(cmd==1)KillSelected(); else if(cmd==2)SuspendSelected(); else if(cmd==3)ResumeSelected();
    else if(cmd==4||cmd==5){
        std::wstring txt;
        {
            std::lock_guard<std::mutex> lk(g.dataMtx);
            const ProcRow* p=nullptr;
            for(auto& r:g.procs) if(r.pid==g.selectedPid){p=&r;break;}
            if(!p&&!g.filtIdx.empty()) p=&g.procs[g.filtIdx[0]];
            if(p){ if(cmd==4){wchar_t b[16];swprintf(b,16,L"%u",p->pid);txt=b;} else txt=p->name; }
        }
        if(!txt.empty()) CopyText(txt);
    }
    RenderFrame();
}

// ---------------------------------------------------------------- hit-test
static bool PtIn(const RECT&r,int x,int y){return x>=r.left&&x<r.right&&y>=r.top&&y<r.bottom;}
static int TabAt(int x,int y){for(int i=0;i<5;i++)if(PtIn(g_lay.tabBtn[i],x,y))return i;return -1;}
static int TitleBtnAt(int x,int y){ // 1=min 2=max 3=close (areas de 30px no canto dir.)
    int th=g_lay.titleBar.bottom-g_lay.titleBar.top;
    int bs=(int)(30.0f*g.dpi/96.0f+0.5f);
    int W=g.pxW;
    if(y<g_lay.titleBar.top||y>=g_lay.titleBar.bottom) return 0;
    if(x>=W-3*bs && x<W-2*bs) return 1;
    if(x>=W-2*bs && x<W-1*bs) return 2;
    if(x>=W-1*bs && x<W)     return 3;
    (void)th;
    return 0;
}
// linha clicada na grade de processos -> indice em filtIdx ou -1
static int ProcRowAt(int x,int y)
{
    if(g.tab!=Tab::Processes) return -1;
    RECT gr=g_lay.gridArea; if(!PtIn(gr,x,y)) return -1;
    // area de linhas = abaixo do header
    int headH=g_lay.headH,rowH=g_lay.rowH;
    int rowsTop=gr.top+headH;
    int rel=y-rowsTop+g.scrollY;
    int pos=rel/rowH;
    if(pos<0||pos>=(int)g.filtIdx.size()) return -1;
    return pos;
}
// usuario clicado na aba Usuarios -> indice em g.users ou -1
static int UserRowAt(int x,int y)
{
    if(g.tab!=Tab::Users) return -1;
    // area da grade: content
    RECT c=g_lay.content; if(!PtIn(c,x,y)) return -1;
    int headH=(int)(24.0f*g.dpi/96.0f+0.5f), rowH=(int)(22.0f*g.dpi/96.0f+0.5f);
    int top=c.top+(int)(8.0f*g.dpi/96.0f+0.5f)+headH;
    // scroll: g.userScroll
    int rel=y-top+g.userScroll;
    int pos=rel/rowH;
    if(pos<0) return -1;
    // precisamos mapear pos -> user (contando filhos expandidos) — simplificacao:
    // conta linhas expandindo
    std::lock_guard<std::mutex> lk(g.dataMtx);
    int line=0;
    for(size_t i=0;i<g.users.size();i++){
        if(line==pos) return (int)i;
        line++;
        if(g.users[i].expanded) line+=(int)g.users[i].pids.size();
    }
    return -1;
}

// ---------------------------------------------------------------- WndProc
LRESULT CALLBACK WndProc(HWND hwnd,UINT msg,WPARAM wp,LPARAM lp)
{
    switch(msg){
    case WM_CREATE:
        g.hwnd=hwnd;
        g.dpi=GetDpiForWindow(hwnd);
        {
            int w=(int)(1280.0f*g.dpi/96.0f), h=(int)(800.0f*g.dpi/96.0f);
            g.pxW=w; g.pxH=h;
        }
        if(!CreateDevice()){ MessageBoxW(hwnd,L"Falha ao criar dispositivo grafico (D3D11/D2D1).",L"Kit Task Manager",MB_ICONERROR); return -1; }
        MetricsInit();
        RebuildProcessView();
        SetTimer(hwnd,1,g.refreshMs,nullptr);   // coleta
        SetTimer(hwnd,2,120,nullptr);           // render sob demanda
        StartCollect();
        RenderFrame();
        return 0;

    case WM_SIZE:
        if(g.swap&&wp!=SIZE_MINIMIZED){
            g.pxW=LOWORD(lp); g.pxH=HIWORD(lp);
            ResizeTargets(g.pxW,g.pxH);
            RenderFrame();
        }
        return 0;

    case WM_DPICHANGED:
        g.dpi=LOWORD(wp);
        SetWindowPos(hwnd,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER);
        // suggested rect
        {
            RECT* r=(RECT*)lp;
            SetWindowPos(hwnd,nullptr,r->left,r->top,r->right-r->left,r->bottom-r->top,
                SWP_NOZORDER|SWP_NOACTIVATE);
        }
        g.pxW=g.pxH=0;
        RECT rc;GetClientRect(hwnd,&rc);g.pxW=rc.right;g.pxH=rc.bottom;
        ResizeTargets(g.pxW,g.pxH);
        RenderFrame();
        return 0;

    case WM_TIMER:
        if(wp==1){
            // historico (amostras de 1s na UI thread)
            auto push=[&](std::deque<float>&d,float v){d.push_back(v);if(d.size()>App::HISTN)d.pop_front();};
            push(g.histCpu,(float)g.sys.cpuPct);
            push(g.histMem,(float)g.sys.memPct);
            push(g.histDisk,(float)g.sys.diskActive);
            push(g.histNet,(float)((g.sys.netSentBps+g.sys.netRecvBps)/1048576.0)); // MB/s
            push(g.histGpu,(float)g.sys.gpuPct);
            StartCollect();
            RebuildProcessView();
            RenderFrame();
        } else if(wp==2){
            RenderFrame(); // mantem UI viva p/ hover/scroll suaves
        }
        return 0;

    case WM_APP_RENDER:
        RebuildProcessView();
        RenderFrame();
        return 0;

    // ---- hit-test custom (janela WS_POPUP): bordas redimensionam, titulo arrasta
    case WM_NCHITTEST:
        {
            POINT pt{GET_X_LPARAM(lp),GET_Y_LPARAM(lp)};
            ScreenToClient(hwnd,&pt);
            int x=pt.x,y=pt.y,W=g.pxW,H=g.pxH;
            const int b=8;
            bool l=x<b,r2=x>=W-b,t=y<b,bo=y>=H-b;
            if(t&&l)return HTTOPLEFT; if(t&&r2)return HTTOPRIGHT;
            if(bo&&l)return HTBOTTOMLEFT; if(bo&&r2)return HTBOTTOMRIGHT;
            if(l)return HTLEFT; if(r2)return HTRIGHT; if(t)return HTTOP; if(bo)return HTBOTTOM;
            if(y<g_lay.titleBar.bottom)return HTCAPTION;
            return HTCLIENT;
        }

    case WM_GETMINMAXINFO:
        {
            auto* mmi=(MINMAXINFO*)lp;
            int mw=(int)(g.minW*g.dpi/96.0f),mh=(int)(g.minH*g.dpi/96.0f);
            mmi->ptMinTrackSize.x=mw;mmi->ptMinTrackSize.y=mh;
        }
        return 0;

    // ---- mouse
    case WM_MOUSEMOVE:
        {
            int x=GET_X_LPARAM(lp),y=GET_Y_LPARAM(lp);
            int nt=TabAt(x,y);
            int nb=TitleBtnAt(x,y);
            bool changed=(nt!=g.hotTab)||(nb!=g.hotBtn);
            g.hotTab=nt; g.hotBtn=nb;
            // cursor
            if(nb|| (y<g_lay.titleBar.bottom&&y>=0)) SetCursor(LoadCursorW(nullptr,IDC_ARROW));
            if(changed) RenderFrame();
        }
        return 0;

    case WM_LBUTTONDOWN:
        {
            int x=GET_X_LPARAM(lp),y=GET_Y_LPARAM(lp);
            SetCapture(hwnd);
            int tb=TitleBtnAt(x,y);
            if(tb==1){ShowWindow(hwnd,SW_MINIMIZE);return 0;}
            if(tb==2){
                if(g.maximized){ShowWindow(hwnd,SW_RESTORE);g.maximized=false;}
                else{
                    WINDOWPLACEMENT wp2{sizeof(wp2)};
                    GetWindowPlacement(hwnd,&wp2);
                    wp2.showCmd=SW_SHOWMAXIMIZED;
                    SetWindowPlacement(hwnd,&wp2);g.maximized=true;
                }
                return 0;
            }
            if(tb==3){DestroyWindow(hwnd);return 0;}
            int t=TabAt(x,y);
            if(t>=0){g.tab=(Tab)t;g.scrollY=0;g.svcScroll=0;g.startScroll=0;g.userScroll=0;RenderFrame();return 0;}
            if(g.tab==Tab::Processes){
                // header sort
                for(size_t c=0;c<g_lay.colHead.size()&&c<g_lay.colX.size();c++){
                    if(PtIn(g_lay.colHead[c],x,y)){
                        int sk=(int)kProcSortKeyOf(c);
                        if(sk>=0){ if(g.sortCol==sk)g.sortAsc=!g.sortAsc; else {g.sortCol=sk;g.sortAsc=(sk==0);} RebuildProcessView(); RenderFrame(); }
                        return 0;
                    }
                }
                // busca focus
                if(PtIn(g_lay.searchBox,x,y)){g.searchFocus=true;SetCapture(hwnd);RenderFrame();return 0;}
                g.searchFocus=false;
                // scrollbars
                if(PtIn(g_lay.vScroll,x,y)){g.dragKind=20;g.dragOff={x,y};return 0;}
                if(PtIn(g_lay.hScroll,x,y)){g.dragKind=21;g.dragOff={x,y};return 0;}
                // linha
                int pos=ProcRowAt(x,y);
                if(pos>=0&&pos<(int)g.filtIdx.size()){
                    int idx=g.filtIdx[pos];
                    std::lock_guard<std::mutex> lk(g.dataMtx);
                    g.selectedPid=g.procs[idx].pid;
                    RenderFrame();
                    return 0;
                }
            } else if(g.tab==Tab::Users){
                int ui=UserRowAt(x,y);
                if(ui>=0){std::lock_guard<std::mutex> lk(g.dataMtx);
                    if(ui<(int)g.users.size())g.users[ui].expanded=!g.users[ui].expanded;
                    RenderFrame();return 0;}
                if(PtIn(g_lay.userScroll,x,y)){g.dragKind=23;g.dragOff={x,y};return 0;}
            } else if(g.tab==Tab::Services){
                if(PtIn(g_lay.svcScroll,x,y)){g.dragKind=22;g.dragOff={x,y};return 0;}
            } else if(g.tab==Tab::Startup){
                if(PtIn(g_lay.startScroll,x,y)){g.dragKind=24;g.dragOff={x,y};return 0;}
            }
        }
        return 0;

    case WM_LBUTTONUP:
        {
            int x=GET_X_LPARAM(lp),y=GET_Y_LPARAM(lp);
            ReleaseCapture();
            g.dragKind=0;
            if(g.tab==Tab::Processes){
                // clique fora da busca tira foco
                if(g.searchFocus&&!PtIn(g_lay.searchBox,x,y)){g.searchFocus=false;RenderFrame();}
            }
        }
        return 0;

    case WM_RBUTTONDOWN:
        {
            int x=GET_X_LPARAM(lp),y=GET_Y_LPARAM(lp);
            if(g.tab==Tab::Processes){
                int pos=ProcRowAt(x,y);
                if(pos>=0&&pos<(int)g.filtIdx.size()){
                    std::lock_guard<std::mutex> lk(g.dataMtx);
                    g.selectedPid=g.procs[g.filtIdx[pos]].pid;
                    RenderFrame();
                }
                POINT sp{x,y};ClientToScreen(hwnd,&sp);
                ShowContextMenu(sp.x,sp.y);
            }
        }
        return 0;

    case WM_MOUSEWHEEL:
        {
            int delta=GET_WHEEL_DELTA_WPARAM(wp)/WHEEL_DELTA;
            int x=GET_X_LPARAM(lp),y=GET_Y_LPARAM(lp);
            POINT sp{x,y};ScreenToClient(hwnd,&sp);x=sp.x;y=sp.y;
            int step=(int)(22.0f*g.dpi/96.0f+0.5f)*3*delta;
            if(g.tab==Tab::Processes){
                if(PtIn(g_lay.gridArea,x,y)||PtIn(g_lay.vScroll,x,y)){
                    g.scrollY=ClampV(g.scrollY-step,0,1<<28);RenderFrame();return 0;
                }
                if(PtIn(g_lay.hScroll,x,y)){g.scrollX=ClampV(g.scrollX-step,0,1<<28);RenderFrame();return 0;}
            } else if(g.tab==Tab::Users){g.userScroll=ClampV(g.userScroll-step,0,1<<28);RenderFrame();return 0;}
            else if(g.tab==Tab::Services){g.svcScroll=ClampV(g.svcScroll-step,0,1<<28);RenderFrame();return 0;}
            else if(g.tab==Tab::Startup){g.startScroll=ClampV(g.startScroll-step,0,1<<28);RenderFrame();return 0;}
        }
        return 0;

    // ---- teclado / texto da busca
    case WM_CHAR:
        if(g.searchFocus&&g.tab==Tab::Processes){
            wchar_t c=(wchar_t)wp;
            if(c==8){ // backspace
                if(!g.searchText.empty())g.searchText.pop_back();
            } else if(c==27){ g.searchText.clear(); g.searchFocus=false; }
            else if(c>=32&&c!=127){ if(g.searchText.size()<80)g.searchText+=c; }
            else return 0;
            RebuildProcessView();RenderFrame();
            return 0;
        }
        return 0;

    case WM_KEYDOWN:
        if(wp==VK_F5){StartCollect();RenderFrame();return 0;}
        if(wp==VK_ESCAPE){if(!g.searchText.empty()){g.searchText.clear();RebuildProcessView();RenderFrame();}return 0;}
        if(wp==VK_DELETE&&g.tab==Tab::Processes){KillSelected();StartCollect();return 0;}
        if(wp=='F'&&(GetKeyState(VK_CONTROL)&0x8000)){g.searchFocus=true;RenderFrame();return 0;}
        break;

    case WM_DESTROY:
        KillTimer(hwnd,1);KillTimer(hwnd,2);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd,msg,wp,lp);
}

// mapeia indice de coluna -> sortKey (-1 = sem ordenacao)
static int kProcSortKeyOf(size_t c)
{
    // mesma ordem de kProcCols em render.cpp
    static const int map_[11]={0,1,2,3,4,5,6,7,8,9,10};
    return c<11?map_[c]:-1;
}

// ---------------------------------------------------------------- WinMain
int WINAPI wWinMain(HINSTANCE inst,HINSTANCE,PWSTR,int)
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    INITCOMMONCONTROLSEX icc{sizeof(icc),ICC_STANDARD_CLASSES};
    InitCommonControlsEx(&icc);

    WNDCLASSEXW wc{sizeof(wc)};
    wc.style=CS_HREDRAW|CS_VREDRAW;
    wc.lpfnWndProc=WndProc;
    wc.hInstance=inst;
    wc.hCursor=LoadCursorW(nullptr,IDC_ARROW);
    wc.lpszClassName=L"KitTaskManagerNative";
    wc.hbrBackground=(HBRUSH)GetStockObject(WHITE_BRUSH);
    RegisterClassExW(&wc);

    int w=(int)(1280.0f*GetDpiForSystem()/96.0f), h=(int)(800.0f*GetDpiForSystem()/96.0f);
    HWND hwnd=CreateWindowExW(0,wc.lpszClassName,L"Kit Task Manager",
        WS_POPUP|WS_VISIBLE,
        CW_USEDEFAULT,CW_USEDEFAULT,w,h,
        nullptr,nullptr,inst,nullptr);
    if(!hwnd)return 1;
    ShowWindow(hwnd,SW_SHOW);
    UpdateWindow(hwnd);

    MSG m;
    while(GetMessageW(&m,nullptr,0,0)){
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
    return 0;
}
