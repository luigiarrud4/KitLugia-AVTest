// tm.h — Kit Task Manager nativo (C++ Win32 + D3D11 + D2D1/DWrite)
// Mesma linguagem do GameOverlayTest: janelas Win32, swapchain DXGI flip-model,
// render imediato via Direct2D + texto via DirectWrite, métricas via Win32/PDH.
// Skin: TMOG "Windows Task Manager" — cinza 3D #D4D0C8, titulo navy #000080,
// caixas com borda #808080, medidores verde-no-preto.
#pragma once

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <windowsx.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <pdh.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <commctrl.h>
#include <string>
#include <vector>
#include <unordered_map>
#include <deque>
#include <algorithm>
#include <mutex>
#include <atomic>
#include <functional>
#include <cwctype>
#include <cstring>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "pdh.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "advapi32.lib")

// ---------------------------------------------------------------- skin TMOG
namespace Skin {
    // paleta identica ao KitTaskManagerWindow.xaml (TmBg*/TmText*/TmGood...)
    static const D2D1_COLOR_F Win        = D2D1::ColorF(0x11/255.f,0x18/255.f,0x23/255.f); // #111823
    static const D2D1_COLOR_F Card       = D2D1::ColorF(0x14/255.f,0x1C/255.f,0x28/255.f); // #141C28
    static const D2D1_COLOR_F Control    = D2D1::ColorF(0x1F/255.f,0x2A/255.f,0x3A/255.f); // #1F2A3A
    static const D2D1_COLOR_F Hover      = D2D1::ColorF(0x24/255.f,0x30/255.f,0x40/255.f); // #243040
    static const D2D1_COLOR_F Press      = D2D1::ColorF(0x26/255.f,0x33/255.f,0x4A/255.f); // #26334A
    static const D2D1_COLOR_F Header     = D2D1::ColorF(0x1A/255.f,0x24/255.f,0x32/255.f); // #1A2432
    static const D2D1_COLOR_F Select     = D2D1::ColorF(0x17/255.f,0x32/255.f,0x4F/255.f); // #17324F
    static const D2D1_COLOR_F Border     = D2D1::ColorF(0x1F/255.f,0x2A/255.f,0x3A/255.f); // #1F2A3A
    static const D2D1_COLOR_F BorderDk   = D2D1::ColorF(0x2C/255.f,0x3A/255.f,0x4E/255.f); // #2C3A4E
    static const D2D1_COLOR_F Accent     = D2D1::ColorF(0x4F/255.f,0xC3/255.f,0xF7/255.f); // #4FC3F7
    static const D2D1_COLOR_F Text       = D2D1::ColorF(0xEE/255.f,0xEE/255.f,0xEE/255.f); // #EEEEEE
    static const D2D1_COLOR_F TextDim    = D2D1::ColorF(0xAA/255.f,0xAA/255.f,0xAA/255.f); // #AAAAAA
    static const D2D1_COLOR_F TextMuted  = D2D1::ColorF(0x88/255.f,0x88/255.f,0x88/255.f); // #888888
    static const D2D1_COLOR_F TextFaint  = D2D1::ColorF(0x66/255.f,0x66/255.f,0x66/255.f); // #666666
    static const D2D1_COLOR_F Well       = D2D1::ColorF(0x0D/255.f,0x13/255.f,0x1A/255.f); // #0D131A
    static const D2D1_COLOR_F MeterLit   = D2D1::ColorF(0x4C/255.f,0xAF/255.f,0x50/255.f); // #4CAF50
    static const D2D1_COLOR_F MeterUnlit = D2D1::ColorF(0x1A/255.f,0x3A/255.f,0x28/255.f);
    static const D2D1_COLOR_F Good       = D2D1::ColorF(0x4C/255.f,0xAF/255.f,0x50/255.f); // #4CAF50
    static const D2D1_COLOR_F Warn       = D2D1::ColorF(1.0f,0x98/255.f,0);                  // #FF9800
    static const D2D1_COLOR_F Bad        = D2D1::ColorF(0xF4/255.f,0x43/255.f,0x36/255.f); // #F44336
    static const D2D1_COLOR_F Ram        = D2D1::ColorF(0xB3/255.f,0x6A/255.f,0xE2/255.f); // #B36AE2
    static const D2D1_COLOR_F Net        = D2D1::ColorF(0x9C/255.f,0x27/255.f,0xB0/255.f); // #9C27B0
    static const D2D1_COLOR_F White      = D2D1::ColorF(1,1,1);
}


// ---------------------------------------------------------------- dados
struct ProcRow {
    DWORD pid=0, ppid=0, sessionId=0;
    std::wstring name, path, user, cmdline;
    std::wstring priority;
    int threads=0, handles=0;
    double cpuPct=0;          // % da capacidade total (0-100)
    double memMB=0, peakMB=0, privMB=0;
    double diskReadBps=0, diskWriteBps=0;
    double netBps=0;          // TCP estimado por PID (0 = sem tráfego medido)
    double gpuPct=0;          // % GPU total por PID (PDH engines)
    ULONGLONG ioRead=0, ioWrite=0;
    bool alive=true;
    bool selected=false;
    // estado anterior p/ deltas (não serializado)
    ULONGLONG prevKernel=0, prevUser=0, prevRead=0, prevWrite=0;
    FILETIME prevTime{};
    bool hasPrev=false;
};

struct SysSample {
    double cpuPct=0, memPct=0, memUsedGB=0, memTotalGB=0;
    double diskActive=0, diskQueue=0, diskLatencyMs=0;
    double diskReadMBps=0, diskWriteMBps=0;
    double netSentBps=0, netRecvBps=0;
    double gpuPct=0;
    int logicalCores=1;
    double freqMhz=0, tempC=-1, powerW=-1;
};

struct SvcRow { std::wstring name, display, status; DWORD pid=0; DWORD state=0; };
struct StartRow { std::wstring name, cmd, location; bool enabled=true; };
struct UserRow { std::wstring name; int session=0; int procCount=0; double cpu=0, memMB=0;
                 std::vector<DWORD> pids; bool expanded=false; };

// abas
enum class Tab { Processes, Performance, Services, Startup, Users };

// ---------------------------------------------------------------- app state
struct App {
    // D3D/D2D/DWrite
    ID3D11Device*        d3d=nullptr;
    IDXGISwapChain1*     swap=nullptr;
    ID2D1Factory1*       d2f=nullptr;
    ID2D1Device*         d2dev=nullptr;
    ID2D1DeviceContext*  dc=nullptr;
    ID2D1Bitmap1*        target=nullptr;
    IDWriteFactory*      dw=nullptr;
    IDWriteTextFormat*   fTitle=nullptr,*fHead=nullptr,*fBody=nullptr,*fSmall=nullptr,*fBig=nullptr,*fMono=nullptr;
    ID2D1SolidColorBrush *bWin=nullptr,*bCard=nullptr,*bCtl=nullptr,*bHov=nullptr,*bPrs=nullptr,
                        *bHeader=nullptr,*bSel=nullptr,*bAccent=nullptr,*bBrd=nullptr,*bBrdD=nullptr,*bTxt=nullptr,
                        *bDim=nullptr,*bMut=nullptr,*bFnt=nullptr,*bWell=nullptr,*bWht=nullptr,
                        *bGood=nullptr,*bWarn=nullptr,*bBad=nullptr,*bRam=nullptr,*bNet=nullptr;

    HWND hwnd=nullptr;
    UINT dpi=96;
    int W=1280, H=800;          // tamanho logico (px ja escalado p/ fisico em pxW/pxH)
    int pxW=0, pxH=0;
    int minW=980, minH=620;
    bool maximized=false;
    RECT restoreRect{};

    Tab tab=Tab::Processes;

    // processos
    std::vector<ProcRow> procs;
    std::vector<ProcRow> filtered;
    std::mutex dataMtx;
    std::atomic<bool> collecting{false};
    DWORD selectedPid=0;
    int scrollY=0, scrollX=0;
    int detailScroll=0;
    std::wstring searchText;
    int sortCol=0; bool sortAsc=false;   // 0=nome
    bool menuOpen=false;

    // performance
    SysSample sys;
    std::deque<float> histCpu, histMem, histDisk, histNet, histGpu;
    static constexpr int HISTN = 120;

    // servicos / startup / usuarios
    std::vector<SvcRow> svcs;
    std::vector<StartRow> starts;
    std::vector<UserRow> users;
    int svcScroll=0, startScroll=0, userScroll=0;

    // input state
    bool dragging=false; POINT dragOff{};
    int dragKind=0; // 0 none,1 move,2 resize edges encoded
    int hotTab=-1, hotBtn=-1;
    bool searchFocus=false;

    // dados filtrados/ordenados (indices em g.procs)
    std::vector<int> filtIdx;
    std::vector<int> visRows;      // indices visiveis na area (preenchido no render)
    int selRowVis=-1;              // posicao na lista visivel

    // timers
    UINT_PTR tRefresh=0, tRender=0;
    std::wstring statusText=L"Pronto";
    int refreshMs=1000;
};
extern App g;

// layout calculado no render e lido pelo input (coordenadas FISICAS)
struct Layout {
    RECT titleBar{}, tabStrip{}, statusBar{}, content{};
    RECT tabBtn[8]{};
    // processos
    RECT searchBox{}, gridArea{}, detailPanel{}, vScroll{}, hScroll{}, colHeader{};
    std::vector<RECT> colHead;      // rects dos headers de coluna
    std::vector<float> colX;        // x inicial de cada coluna (fisico)
    std::vector<float> colW;        // largura de cada coluna (fisico)
    float gridTotalW=0;
    int rowH=0, headH=0, toolH=0;
    // scrolls das outras abas
    RECT svcScroll{}, startScroll{}, userScroll{};
    RECT svcArea{}, startArea{}, userArea{};
    RECT perfArea{};
};
extern Layout g_lay;

template<typename T> inline T ClampV(T v,T a,T b){ return v<a?a:(v>b?b:v); }

// metrics.cpp
void MetricsInit();
void CollectAll();                 // snapshot completo (worker)
void CollectSystemOnly();
void CollectServices();
void CollectStartup();
void CollectUsers();
std::wstring FormatBytes(double b);
std::wstring FormatBps(double b);
std::wstring FormatPct(double v);
const wchar_t* PrioName(DWORD p);

// render.cpp
void RenderFrame();
void EnsureTargets();
void ResizeTargets(int w,int h);
void RebuildProcessView();
float S(float v);                   // escala DPI

// main.cpp
LRESULT CALLBACK WndProc(HWND,UINT,WPARAM,LPARAM);
void DoRefresh();
void ShowContextMenu(int x,int y);
void KillSelected(); void SuspendSelected(); void ResumeSelected();
