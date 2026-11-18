// render.cpp — render D2D1/DWrite do Kit Task Manager nativo (skin TMOG)
// Tudo e' desenhado por frame no device context; texto via DirectWrite.
#include "tm.h"
#include <dwrite.h>

Layout g_lay;
App g;

// ---------------------------------------------------------------- utilidades
float S(float v){ return v * (float)g.dpi / 96.0f; }
static int Si(int v){ return (int)(v * (float)g.dpi / 96.0f + 0.5f); }

static inline D2D1_RECT_F R(const RECT& r){ return D2D1::RectF((float)r.left,(float)r.top,(float)r.right,(float)r.bottom); }
static inline D2D1_RECT_F Rf(float x,float y,float w,float h){ return D2D1::RectF(x,y,x+w,y+h); }

static void FillR(ID2D1DeviceContext* dc, ID2D1SolidColorBrush* b, const RECT& r){ dc->FillRectangle(R(r), b); }
static void FillRf(ID2D1DeviceContext* dc, ID2D1SolidColorBrush* b, float x,float y,float w,float h){ dc->FillRectangle(Rf(x,y,w,h), b); }
static void FrameR(ID2D1DeviceContext* dc, ID2D1SolidColorBrush* b, const RECT& r, float th=1.0f){ dc->DrawRectangle(R(r), b, th); }
static void FrameRf(ID2D1DeviceContext* dc, ID2D1SolidColorBrush* b, float x,float y,float w,float h, float th=1.0f){ dc->DrawRectangle(Rf(x,y,w,h), b, th); }
static void LineH(ID2D1DeviceContext* dc, ID2D1SolidColorBrush* b, float x,float y,float w){ dc->DrawLine(D2D1::Point2F(x,y),D2D1::Point2F(x+w,y),b,1.0f); }
static void LineV(ID2D1DeviceContext* dc, ID2D1SolidColorBrush* b, float x,float y,float h){ dc->DrawLine(D2D1::Point2F(x,y),D2D1::Point2F(x,y+h),b,1.0f); }

// texto: mede e centraliza na vertical; ellipsis ja vem do formato
static void DrawTxt(ID2D1DeviceContext* dc, IDWriteTextFormat* fmt, const wchar_t* s,
                    float x,float y,float w,float h, ID2D1SolidColorBrush* b,
                    DWRITE_TEXT_ALIGNMENT align=DWRITE_TEXT_ALIGNMENT_LEADING, bool centerV=true)
{
    if (!s || !*s) return;
    if (w<=0 || h<=0) return;
    IDWriteTextLayout* lay=nullptr;
    if (FAILED(g.dw->CreateTextLayout(s,(UINT32)wcslen(s),fmt,w,h,&lay))) return;
    lay->SetTextAlignment(align);
    // os formatos ja tem ParagraphAlignment=CENTER: o layout de altura h centraliza na vertical
    dc->DrawTextLayout(D2D1::Point2F(x,y), lay, b);
    lay->Release();
}
static void DrawTxtR(ID2D1DeviceContext* dc, IDWriteTextFormat* fmt, const wchar_t* s,
                     const RECT& r, ID2D1SolidColorBrush* b, DWRITE_TEXT_ALIGNMENT align=DWRITE_TEXT_ALIGNMENT_LEADING)
{ DrawTxt(dc,fmt,s,(float)r.left,(float)r.top,(float)(r.right-r.left),(float)(r.bottom-r.top),b,align,true); }

// ---------------------------------------------------------------- targets/brushes
void EnsureTargets()
{
    if (!g.swap || !g.dc) return;
    if (g.target) { g.target->Release(); g.target=nullptr; }
    IDXGISurface* surf=nullptr;
    g.swap->GetBuffer(0,__uuidof(IDXGISurface),(void**)&surf);
    D2D1_BITMAP_PROPERTIES1 bp = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_TARGET|D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
    g.dc->CreateBitmapFromDxgiSurface(surf,&bp,&g.target);
    surf->Release();
    g.dc->SetTarget(g.target);
    g.dc->SetDpi((float)g.dpi,(float)g.dpi);
}
void ResizeTargets(int w,int h)
{
    if (!g.swap) return;
    g.dc->SetTarget(nullptr);
    g.target->Release(); g.target=nullptr;
    g.swap->ResizeBuffers(0,(UINT)w,(UINT)h,DXGI_FORMAT_UNKNOWN,0);
    EnsureTargets();
}

static void MakeFmt(IDWriteTextFormat** f, float size, bool bold, DWRITE_FONT_WEIGHT w=DWRITE_FONT_WEIGHT_NORMAL)
{
    if (*f) return;
    g.dw->CreateTextFormat(L"Segoe UI",nullptr,w,DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,
        size,L"pt-BR",f);
    (*f)->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    DWRITE_TRIMMING trm{DWRITE_TRIMMING_GRANULARITY_CHARACTER,0x2026,1};
    (*f)->SetTrimming(&trm,nullptr);
    (*f)->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
}
static void MakeBrush(ID2D1SolidColorBrush** b, D2D1_COLOR_F c)
{
    if (*b) return;
    g.dc->CreateSolidColorBrush(c,b);
}
static void EnsureBrushes()
{
    MakeBrush(&g.bWin,  Skin::Win);
    MakeBrush(&g.bCard, Skin::Card);
    MakeBrush(&g.bCtl,  Skin::Control);
    MakeBrush(&g.bHov,  Skin::Hover);
    MakeBrush(&g.bPrs,  Skin::Press);
    MakeBrush(&g.bHeader,Skin::Header);
    MakeBrush(&g.bSel,   Skin::Select);
    MakeBrush(&g.bAccent,Skin::Accent);
    MakeBrush(&g.bGood,  Skin::Good);
    MakeBrush(&g.bWarn,  Skin::Warn);
    MakeBrush(&g.bBad,   Skin::Bad);
    MakeBrush(&g.bRam,   Skin::Ram);
    MakeBrush(&g.bNet,   Skin::Net);
    MakeBrush(&g.bBrd,  Skin::Border);
    MakeBrush(&g.bBrdD, Skin::BorderDk);
    MakeBrush(&g.bTxt,  Skin::Text);
    MakeBrush(&g.bDim,  Skin::TextDim);
    MakeBrush(&g.bMut,  Skin::TextMuted);
    MakeBrush(&g.bFnt,  Skin::TextFaint);
    MakeBrush(&g.bWell, Skin::Well);
    MakeBrush(&g.bWht,  Skin::White);
    MakeFmt(&g.fTitle, S(13),true, DWRITE_FONT_WEIGHT_SEMI_BOLD);
    MakeFmt(&g.fHead,  S(10),true, DWRITE_FONT_WEIGHT_SEMI_BOLD);
    MakeFmt(&g.fBody,  S(11),false);
    MakeFmt(&g.fSmall, S(9),false);
    MakeFmt(&g.fBig,   S(26),true, DWRITE_FONT_WEIGHT_BOLD);
    MakeFmt(&g.fMono,  S(10),false);
}

// ---------------------------------------------------------------- colunas processos
struct Col { const wchar_t* name; float w; int sortKey; }; // sortKey: -1 = sem sort
static const Col kProcCols[] = {
    {L"Nome",        200, 0},
    {L"PID",          64, 1},
    {L"Usuário",     104, 2},
    {L"CPU",          58, 3},
    {L"Memória",      80, 4},
    {L"Disco",        88, 5},
    {L"Rede",         88, 6},
    {L"GPU",          58, 7},
    {L"Threads",      58, 8},
    {L"Handles",      66, 9},
    {L"Prioridade",   92,10},
};
static const int kNProcCols = (int)(sizeof(kProcCols)/sizeof(kProcCols[0]));

// ordenacao/filtragem (chamada apos coleta)
void RebuildProcessView()
{
    std::lock_guard<std::mutex> lk(g.dataMtx);
    std::wstring q = g.searchText;
    for (auto& c : q) c = (wchar_t)towlower(c);
    g.filtIdx.clear();
    for (int i=0;i<(int)g.procs.size();i++) {
        const ProcRow& p = g.procs[i];
        if (!q.empty()) {
            std::wstring n=p.name, u=p.user, pa=p.path;
            for (auto& c:n) c=(wchar_t)towlower(c);
            for (auto& c:u) c=(wchar_t)towlower(c);
            for (auto& c:pa) c=(wchar_t)towlower(c);
            wchar_t pidb[16]; swprintf(pidb,16,L"%u",p.pid);
            if (n.find(q)==std::wstring::npos && u.find(q)==std::wstring::npos &&
                pa.find(q)==std::wstring::npos && wcsstr(pidb,q.c_str())==nullptr) continue;
        }
        g.filtIdx.push_back(i);
    }
    // sort
    auto key=[&](int idx)->double{
        const ProcRow& p=g.procs[idx];
        switch(g.sortCol){
            case 0: return 0; case 1: return p.pid; case 2: return 0;
            case 3: return p.cpuPct; case 4: return p.memMB;
            case 5: return p.diskReadBps+p.diskWriteBps; case 6: return p.netBps;
            case 7: return p.gpuPct; case 8: return p.threads; case 9: return p.handles;
            default: return 0;
        }
    };
    auto skey=[&](int idx)->std::wstring{
        const ProcRow& p=g.procs[idx];
        switch(g.sortCol){ case 0:return p.name; case 2:return p.user; case 10:return p.priority; default:return L""; }
    };
    std::sort(g.filtIdx.begin(),g.filtIdx.end(),[&](int a,int b){
        if (g.sortCol==1||g.sortCol==3||g.sortCol==4||g.sortCol==5||g.sortCol==6||g.sortCol==7||g.sortCol==8||g.sortCol==9){
            double ka=key(a),kb=key(b);
            if (ka!=kb) return g.sortAsc? ka<kb : ka>kb;
        } else {
            int c=_wcsicmp(skey(a).c_str(),skey(b).c_str());
            if (c!=0) return g.sortAsc? c<0 : c>0;
        }
        return g.procs[a].pid < g.procs[b].pid;
    });
}

// ---------------------------------------------------------------- desenho: titulo + abas
static const wchar_t* kTabNames[] = {L"Processos",L"Desempenho",L"Serviços",L"Inicialização",L"Usuários"};

static void DrawChrome()
{
    auto* dc=g.dc;
    int W=g.pxW,H=g.pxH;
    int th=Si(30);                       // altura da barra de titulo
    // barra navy
    FillRf(dc,g.bHeader,0,0,(float)W,(float)th);
    // titulo
    DrawTxt(dc,g.fTitle,L"Kit Task Manager",Si(10),(th-Si(16))*0.5f,300,Si(20),g.bTxt);
    // botoes min/max/close
    int bs=Si(30);
    auto drawBtn=[&](int idx,const wchar_t* glyph,bool close){
        int x=W-bs*(3-idx);
        bool hot = (g.hotBtn==idx+1);
        bool press = (g.dragKind==10+idx+1);
        D2D1_COLOR_F bg = close ? (press?Skin::Bad:(hot?Skin::Bad:D2D1::ColorF(0,0,0,0)))
                                 : (hot?Skin::Hover:D2D1::ColorF(0,0,0,0));
        if (bg.a>0) { ID2D1SolidColorBrush* tb=nullptr; g.dc->CreateSolidColorBrush(bg,&tb); FillRf(dc,tb,(float)x,0,(float)bs,(float)th); tb->Release(); }
        DrawTxt(dc,g.fBody,glyph,(float)x,0,(float)bs,(float)th,g.bTxt,DWRITE_TEXT_ALIGNMENT_CENTER);
    };
    drawBtn(0,L"–",false); drawBtn(1,L"▢",false); drawBtn(2,L"✕",true);
    g_lay.titleBar={0,0,W,th};

    // faixa de abas
    int ty=th, ah=Si(30);
    FillRf(dc,g.bWin,0,(float)ty,(float)W,(float)ah);
    LineH(dc,g.bBrd,0,(float)(ty+ah),(float)W);
    int tx=Si(8);
    int bw=Si(96), bh=Si(22), by=ty+Si(4);
    for (int i=0;i<5;i++) {
        RECT br{tx,by,tx+bw,by+bh};
        g_lay.tabBtn[i]=br;
        bool sel=(g.tab==(Tab)i), hot=(g.hotTab==i);
        if (sel) { FillR(dc,g.bSel,br); FrameR(dc,g.bAccent,br,1); }
        else if (hot) FillR(dc,g.bHov,br);
        else FillR(dc,g.bCard,br);
        if (!sel) FrameR(dc,g.bBrd,br,1);
        DrawTxt(dc,g.fHead,kTabNames[i],br.left,br.top,(float)(br.right-br.left),(float)(br.bottom-br.top),
                sel?g.bTxt:g.bDim,DWRITE_TEXT_ALIGNMENT_CENTER);
        tx+=bw+Si(4);
    }
    g_lay.tabStrip={0,ty,W,ty+ah};
}

static void DrawStatusBar()
{
    int H=g.pxH, sh=Si(24);
    int y=H-sh;
    FillRf(g.dc,g.bWin,0,(float)y,(float)g.pxW,(float)sh);
    LineH(g.dc,g.bBrd,0,(float)y,(float)g.pxW);
    wchar_t buf[256];
    int n=(int)g.procs.size();
    const wchar_t* tn=kTabNames[(int)g.tab];
    swprintf(buf,256,L"%s · %d processos · F5=Atualizar · Ctrl+F=Buscar · Esc=Limpar busca",tn,n);
    DrawTxt(g.dc,g.fSmall,buf,Si(8),(float)y,1000,Si(24),g.bDim);
    g_lay.statusBar={0,y,g.pxW,H};
}

// ---------------------------------------------------------------- processos
static int ContentTop(){ return g_lay.tabStrip.bottom; }
static int ContentBottom(){ return g_lay.statusBar.top; }

// monta g_lay das colunas (x/y/largura fisicos) — chamado 1x por frame antes de desenhar
static void LayoutProcGrid(int x,int y,int w,int h)
{
    g_lay.gridArea={x,y,w,h};
    // area util: desconta scrollbar vertical (14) e toolbar ja fica fora
    int vw=w-Si(14);
    // colunas: largura fixa; se total > vw, scroll horizontal
    float total=0; for(int i=0;i<kNProcCols;i++) total+=S(kProcCols[i].w);
    g_lay.gridTotalW=total;
    g_lay.colX.clear(); g_lay.colW.clear(); g_lay.colHead.clear();
    float cx=(float)x;
    for(int i=0;i<kNProcCols;i++){
        float cw=S(kProcCols[i].w);
        g_lay.colX.push_back(cx-g.scrollX);
        g_lay.colW.push_back(cw);
        cx+=cw;
    }
}

static std::wstring ProcCellText(const ProcRow& p,int col)
{
    wchar_t b[64];
    switch(col){
        case 0: return p.name;
        case 1: swprintf(b,64,L"%u",p.pid); return b;
        case 2: return p.user.empty()?L"—":p.user;
        case 3: swprintf(b,64,L"%.1f",p.cpuPct); return b;
        case 4: return FormatBytes(p.memMB*1048576.0);
        case 5: { double t=p.diskReadBps+p.diskWriteBps; if(t<=0)return L"—"; return FormatBps(t); }
        case 6: return p.netBps>0? FormatBps(p.netBps) : L"—";
        case 7: { if(p.gpuPct<=0)return L"—"; swprintf(b,64,L"%.0f",p.gpuPct); return b; }
        case 8: swprintf(b,64,L"%d",p.threads); return b;
        case 9: swprintf(b,64,L"%d",p.handles); return b;
        case 10: return p.priority.empty()?L"—":p.priority;
    }
    return L"";
}

static void DrawProcesses()
{
    auto* dc=g.dc;
    int x0=g_lay.content.left+Si(8), y0=ContentTop()+Si(8);
    int W=g_lay.content.right-g_lay.content.left-Si(16);
    // ---- toolbar: busca ----
    int toolH=Si(26);
    g_lay.searchBox={x0,y0,Si(260)+x0,y0+toolH};
    {
        RECT sb=g_lay.searchBox;
        FillR(dc,g.bCtl,sb); FrameR(dc,g.bBrd,sb,1);
        wchar_t disp[256];
        if (g.searchText.empty()) swprintf(disp,256,L"  Filtrar processos…");
        else swprintf(disp,256,L"%s%s",g.searchText.c_str(), g.searchFocus?L"|":L"");
        DrawTxt(dc,g.fBody,disp,(float)sb.left+Si(4),(float)sb.top,(float)(sb.right-sb.left)-Si(8),(float)(sb.bottom-sb.top),
                g.searchText.empty()?g.bFnt:g.bTxt);
    }
    int gy=y0+toolH+Si(4);                 // topo do grid (header)
    int gh=ContentBottom()-gy-Si(8);       // altura total do grid
    int headH=Si(24), rowH=Si(22);
    g_lay.headH=headH; g_lay.rowH=rowH;

    // painel de detalhes a direita
    int detailW = W>Si(900)? Si(300) : 0;
    int gridW = W - detailW - Si(14);      // desconta vscroll
    g_lay.detailPanel = detailW? RECT{ x0+gridW+Si(8), gy, x0+W, ContentBottom()-Si(4) } : RECT{0,0,0,0};

    LayoutProcGrid(x0,gy,gridW,gh-headH);

    // ---- header ----
    int hy=gy;
    // fundo do header
    FillRf(dc,g.bHeader,(float)x0,(float)hy,(float)gridW,(float)headH);
    LineH(dc,g.bBrdD,(float)x0,(float)(hy+headH),(float)gridW);
    {
        // clip no header
        auto clip=D2D1::RectF((float)x0,(float)hy,(float)(x0+gridW),(float)(hy+headH));
        dc->PushAxisAlignedClip(clip,D2D1_ANTIALIAS_MODE_ALIASED);
        for(int c=0;c<kNProcCols;c++){
            float cx=g_lay.colX[c], cw=g_lay.colW[c];
            if (cx+cw<x0 || cx>x0+gridW) continue;
            RECT hr{(int)cx,(int)hy,(int)(cx+cw),(int)(hy+headH)};
            g_lay.colHead.push_back(hr);
            wchar_t t[64]; swprintf(t,64,L"%s%s",kProcCols[c].name, g.sortCol==kProcCols[c].sortKey&&kProcCols[c].sortKey>=0?(g.sortAsc?L" ▲":L" ▼"):L"");
            DrawTxt(dc,g.fHead,t,(float)hr.left+Si(6),(float)hr.top,(float)(hr.right-hr.left)-Si(10),(float)headH,g.bDim);
            LineV(dc,g.bBrd,(float)hr.right,(float)hy,(float)headH);
        }
        dc->PopAxisAlignedClip();
    }
    // linha divisoria header/linhas
    // ---- linhas ----
    int rowsTop=hy+headH;
    int viewH=gh-headH;
    int totalRows=(int)g.filtIdx.size();
    int maxScroll=std::max(0,totalRows*rowH-viewH);
    g.scrollY=ClampV(g.scrollY,0,maxScroll);
    // visRows
    g.visRows.clear();
    int firstRow=g.scrollY/rowH;
    int lastRow=std::min(totalRows-1,(g.scrollY+viewH)/rowH);
    for(int i=firstRow;i<=lastRow && i<totalRows;i++) g.visRows.push_back(g.filtIdx[i]);

    // zebra + selecao + hover
    dc->PushAxisAlignedClip(D2D1::RectF((float)x0,(float)rowsTop,(float)(x0+gridW),(float)(rowsTop+viewH)),D2D1_ANTIALIAS_MODE_ALIASED);
    for(int vi=0;vi<(int)g.visRows.size();vi++){
        int idx=g.visRows[vi];
        const ProcRow& p=g.procs[idx];
        int ry=rowsTop+vi*rowH - (g.scrollY%rowH);
        // na verdade ry deve ser rowsTop + (idxPos*rowH) - scrollY; recomputa:
        int pos=firstRow+vi;
        ry=rowsTop + pos*rowH - g.scrollY;
        bool sel=(p.pid==g.selectedPid);
        bool hot=false; // hover por linha: calculado no input -> simplificacao: sem hover de linha
        if (sel) FillRf(dc,g.bSel,(float)x0,(float)ry,(float)gridW,(float)rowH);
        else if ((pos%2)==0) FillRf(dc,g.bWin,(float)x0,(float)ry,(float)gridW,(float)rowH);
        // celulas
        for(int c=0;c<kNProcCols;c++){
            float cx=g_lay.colX[c], cw=g_lay.colW[c];
            if (cx+cw<x0 || cx>x0+gridW) continue;
            float tx=cx+Si(6);
            float txw=cw-Si(10);
            std::wstring txt=ProcCellText(p,c);
            ID2D1SolidColorBrush* tb = (c==0? g.bTxt : g.bDim);
            if (c==0) tb=g.bTxt;
            if (sel) tb=g.bTxt;
            DrawTxt(dc,g.fBody,txt.c_str(),tx,(float)ry,txw,(float)rowH,tb);
        }
        LineH(dc,g.bBrd,(float)x0,(float)(ry+rowH),(float)gridW);
    }
    dc->PopAxisAlignedClip();
    // borda do grid
    FrameRf(dc,g.bBrd,(float)x0,(float)gy,(float)gridW,(float)(gh-headH),1);

    // ---- scrollbar vertical ----
    int vx=x0+gridW, vy=rowsTop, vh=viewH, vw=Si(14);
    g_lay.vScroll={vx,vy,vx+vw,vy+vh};
    {
        FillR(dc,g.bCtl,g_lay.vScroll); FrameR(dc,g.bBrd,g_lay.vScroll,1);
        if (totalRows*rowH>viewH && viewH>0) {
            float thumbH=std::max((float)Si(20),(float)viewH*viewH/(float)(totalRows*rowH));
            float ty2=(float)vy + ((float)g.scrollY/(float)maxScroll)*((float)vh-thumbH);
            FillRf(dc,g.bBrdD,(float)(vx+2),ty2,(float)(vw-4),thumbH);
        }
    }
    // ---- scrollbar horizontal (se precisar) ----
    g_lay.hScroll={0,0,0,0};
    if (g_lay.gridTotalW>(float)gridW) {
        int hx=x0, hy2=rowsTop+viewH, hw=gridW, hh=Si(12);
        g_lay.hScroll={hx,hy2,hx+hw,hy2+hh};
        FillR(dc,g.bCtl,g_lay.hScroll); FrameR(dc,g.bBrd,g_lay.hScroll,1);
        float thumbW=std::max((float)Si(30),(float)hw*hw/g_lay.gridTotalW);
        float maxSX=g_lay.gridTotalW-(float)gridW;
        float tx2=(float)hx + (g.scrollX/std::max(1.0f,maxSX))*((float)hw-thumbW);
        FillRf(dc,g.bBrdD,tx2,(float)(hy2+2),thumbW,(float)(hh-4));
    }

    // ---- painel de detalhes ----
    if (detailW>0) {
        RECT dp=g_lay.detailPanel;
        FillR(dc,g.bCard,dp); FrameR(dc,g.bBrd,dp,1);
        int dx=dp.left+Si(10), dy=dp.top+Si(8);
        const ProcRow* sel=nullptr;
        for(auto& p:g.procs) if(p.pid==g.selectedPid){sel=&p;break;}
        if (!sel) DrawTxt(dc,g.fBody,L"Selecione um processo",(float)dx,(float)dy,(float)(dp.right-dx)-Si(8),(float)(dp.bottom-dy),g.bDim);
        else {
            int lh=Si(20);
            auto line=[&](const wchar_t* k,const wchar_t* v){
                DrawTxt(dc,g.fSmall,k,(float)dx,(float)dy,(float)(dp.right-dx)-Si(16),(float)lh,g.bMut);
                DrawTxt(dc,g.fBody,v? v:L"—",(float)dx+Si(90),(float)dy,(float)(dp.right-dx)-Si(100),(float)lh,g.bTxt);
                dy+=lh;
            };
            DrawTxt(dc,g.fHead,sel->name.c_str(),(float)dx,(float)dy,(float)(dp.right-dx)-Si(8),(float)Si(24),g.bTxt);
            dy+=Si(26);
            wchar_t b[128];
            swprintf(b,128,L"PID %u",sel->pid); line(L"PID",b);
            swprintf(b,128,L"%.1f%%",sel->cpuPct); line(L"CPU",b);
            line(L"Memória (WS)",FormatBytes(sel->memMB*1048576.0).c_str());
            line(L"Memória (pico)",FormatBytes(sel->peakMB*1048576.0).c_str());
            line(L"Memória (privada)",FormatBytes(sel->privMB*1048576.0).c_str());
            double disk=sel->diskReadBps+sel->diskWriteBps;
            line(L"Disco",disk>0?FormatBps(disk).c_str():L"—");
            line(L"Rede",sel->netBps>0?FormatBps(sel->netBps).c_str():L"—");
            if(sel->gpuPct>0){swprintf(b,128,L"%.0f%%",sel->gpuPct);line(L"GPU",b);}
            swprintf(b,128,L"%d",sel->threads); line(L"Threads",b);
            swprintf(b,128,L"%d",sel->handles); line(L"Handles",b);
            line(L"Prioridade",sel->priority.c_str());
            line(L"Usuário",sel->user.c_str());
            line(L"Caminho",sel->path.c_str());
        }
    }
}

// ---------------------------------------------------------------- desempenho
static void DrawGraph(ID2D1DeviceContext* dc,const std::deque<float>& h,float max,RECT w)
{
    FillR(dc,g.bWell,w);
    int n=(int)h.size();
    if (n<2) return;
    float x0=(float)w.left,x1=(float)w.right,y0=(float)w.top,y1=(float)w.bottom;
    float step=(x1-x0)/std::max(1,n-1);
    // linha zero
    LineH(dc,g.bBrdD,x0,y1,x1-x0);
    // polyline verde-no-preto
    ID2D1PathGeometry* geo=nullptr;
    g.d2f->CreatePathGeometry(&geo);
    ID2D1GeometrySink* sink=nullptr;
    geo->Open(&sink);
    for(int i=0;i<n;i++){
        float v=ClampV(h[i],0.0f,max);
        float y=y1-(v/max)*(y1-y0-2)-1;
        float x=x0+i*step;
        if(i==0)sink->BeginFigure(D2D1::Point2F(x,y),D2D1_FIGURE_BEGIN_HOLLOW);
        else sink->AddLine(D2D1::Point2F(x,y));
    }
    sink->EndFigure(D2D1_FIGURE_END_OPEN);
    sink->Close();sink->Release();
    ID2D1SolidColorBrush* gb=nullptr;g.dc->CreateSolidColorBrush(Skin::Good,&gb);
    ID2D1StrokeStyle* ss=nullptr;g.d2f->CreateStrokeStyle(D2D1::StrokeStyleProperties(D2D1_CAP_STYLE_FLAT,D2D1_CAP_STYLE_FLAT,D2D1_CAP_STYLE_FLAT,D2D1_LINE_JOIN_MITER),nullptr,0,&ss);
    dc->DrawGeometry(geo,gb,1.2f,ss);
    gb->Release();ss->Release();geo->Release();
}

static void DrawPerfCard(int x,int y,int w,int h,const wchar_t* title,float pct,const std::deque<float>& hist,float max,const wchar_t* sub)
{
    auto* dc=g.dc;
    FillR(dc,g.bCard,RECT{x,y,x+w,y+h}); FrameR(dc,g.bBrd,RECT{x,y,x+w,y+h},1);
    int pad=Si(8);
    DrawTxt(dc,g.fHead,title,(float)(x+pad),(float)(y+pad),(float)w-2*pad,Si(16),g.bDim);
    wchar_t pb[32]; swprintf(pb,32,L"%.0f%%",pct);
    DrawTxt(dc,g.fBig,pb,(float)(x+pad),(float)(y+pad+Si(14)),(float)w-2*pad,Si(30), pct>95?g.bBad:(pct>80?g.bWarn:g.bGood));
    int gy=y+pad+Si(48), gh=h-gy+y-pad-Si(20);
    RECT well{x+pad,gy,x+w-pad,gy+std::max((float)Si(40),(float)gh)};
    DrawGraph(dc,hist,max,well);
    if (sub) DrawTxt(dc,g.fSmall,sub,(float)(x+pad),(float)(well.bottom+Si(2)),(float)w-2*pad,Si(14),g.bMut);
}

static void DrawPerformance()
{
    int x0=g_lay.content.left+Si(8), y0=ContentTop()+Si(8);
    int W=g_lay.content.right-g_lay.content.left-Si(16);
    int H=ContentBottom()-y0-Si(8);
    int gap=Si(8);
    int cw=(W-gap)/3, ch=(H-gap)/2;
    // linha 1: CPU, Memoria, Disco ; linha 2: Rede, GPU, (vazio p/ clock/temp/power)
    DrawPerfCard(x0,y0,cw,ch,L"CPU",(float)g.sys.cpuPct,g.histCpu,100,nullptr);
    wchar_t sub[128];
    swprintf(sub,128,L"%.1f / %.1f GB",g.sys.memUsedGB,g.sys.memTotalGB);
    DrawPerfCard(x0+cw+gap,y0,cw,ch,L"Memória",(float)g.sys.memPct,g.histMem,100,sub);
    swprintf(sub,128,L"R %.1f · W %.1f MB/s",g.sys.diskReadMBps,g.sys.diskWriteMBps);
    DrawPerfCard(x0+2*(cw+gap),y0,cw,ch,L"Disco",(float)g.sys.diskActive,g.histDisk,100,sub);
    swprintf(sub,128,L"%.1f MB/s",g.sys.netSentBps/1048576.0);
    DrawPerfCard(x0,y0+ch+gap,cw,ch,L"Rede",0,g.histNet,50*1048576.0f,sub);
    swprintf(sub,128,L"fila %.1f · lat %.1f ms",g.sys.diskQueue,g.sys.diskLatencyMs);
    DrawPerfCard(x0+cw+gap,y0+ch+gap,cw,ch,L"GPU",(float)g.sys.gpuPct,g.histGpu,100,sub);
    // cartao extra: relogio/temp/potencia (mostra "—" na v1)
    DrawPerfCard(x0+2*(cw+gap),y0+ch+gap,cw,ch,L"Relógio / Temp / Potência",0,g.histCpu,100,L"—");
}

// ---------------------------------------------------------------- servicos
static const wchar_t* SvcStateName(DWORD s){
    switch(s){case SERVICE_RUNNING:return L"Em execução";case SERVICE_STOPPED:return L"Parado";
        case SERVICE_START_PENDING:return L"Iniciando";case SERVICE_STOP_PENDING:return L"Parando";
        case SERVICE_PAUSE_PENDING:return L"Pausando";case SERVICE_PAUSED:return L"Pausado";
        case SERVICE_CONTINUE_PENDING:return L"Continuando";default:return L"—";}
}
static void DrawSimpleGrid(int x,int y,int w,int h,
    const std::vector<std::wstring>& cols,float* colWs,int ncols,
    std::function<int()> countFn,std::function<std::wstring(int,int)> cellFn,
    int& scroll,RECT* scrollRect)
{
    auto* dc=g.dc;
    int headH=Si(24),rowH=Si(22);
    // header
    FillR(dc,g.bHeader,RECT{x,y,x+w,y+headH});
    LineH(dc,g.bBrdD,(float)x,(float)(y+headH),(float)w);
    float cx=(float)x;
    for(int c=0;c<ncols;c++){ float cw=colWs[c]*S(1); DrawTxt(dc,g.fHead,cols[c].c_str(),cx+Si(6),(float)y,cw-Si(10),(float)headH,g.bDim); cx+=cw; LineV(dc,g.bBrd,cx,(float)y,(float)headH);}
    int rowsTop=y+headH, viewH=h-headH;
    int total=countFn();
    int maxScroll=std::max(0,total*rowH-viewH);
    scroll=ClampV(scroll,0,maxScroll);
    int first=scroll/rowH, last=std::min(total-1,(scroll+viewH)/rowH);
    dc->PushAxisAlignedClip(D2D1::RectF((float)x,(float)rowsTop,(float)(x+w),(float)(rowsTop+viewH)),D2D1_ANTIALIAS_MODE_ALIASED);
    for(int i=first;i<=last&&i<total;i++){
        int ry=rowsTop+i*rowH-scroll;
        if(i%2==0)FillRf(dc,g.bWin,(float)x,(float)ry,(float)w,(float)rowH);
        float ccx=(float)x;
        for(int c=0;c<ncols;c++){
            float cw=colWs[c]*S(1);
            std::wstring t=cellFn(i,c);
            DrawTxt(dc,g.fBody,t.c_str(),ccx+Si(6),(float)ry,cw-Si(10),(float)rowH,g.bTxt);
            ccx+=cw;
        }
        LineH(dc,g.bBrd,(float)x,(float)(ry+rowH),(float)w);
    }
    dc->PopAxisAlignedClip();
    FrameRf(dc,g.bBrd,(float)x,(float)y,(float)w,(float)h,1);
    // vscroll
    if(scrollRect){*scrollRect=RECT{x+w-Si(14),rowsTop,x+w,rowsTop+viewH};
        FillR(dc,g.bCtl,*scrollRect);FrameR(dc,g.bBrd,*scrollRect,1);
        if(total*rowH>viewH&&viewH>0){float th=std::max((float)Si(20),(float)viewH*viewH/(float)(total*rowH));
            float ty=(float)rowsTop+((float)scroll/std::max(1,maxScroll))*((float)viewH-th);
            FillRf(dc,g.bBrdD,(float)(x+w-Si(12)),ty,(float)(Si(12)-2),th);}
    }
}

static void DrawServices(){
    int x0=g_lay.content.left+Si(8),y0=ContentTop()+Si(8);
    int W=g_lay.content.right-g_lay.content.left-Si(16),H=ContentBottom()-y0-Si(8);
    static const wchar_t* cols[]={L"Nome",L"Nome de exibição",L"PID",L"Status"};
    float ws[]={300,300,80,140};
    std::vector<std::wstring> colsv(cols,cols+4);
    g_lay.svcArea={x0,y0,x0+W-Si(14),y0+H};
    DrawSimpleGrid(x0,y0,W-Si(14),H,colsv,ws,4,
        [](){std::lock_guard<std::mutex> lk(g.dataMtx);return (int)g.svcs.size();},
        [](int i,int c){std::lock_guard<std::mutex> lk(g.dataMtx);const SvcRow&s=g.svcs[i];
            wchar_t b[32];
            switch(c){case 0:return s.name;case 1:return s.display;case 2:swprintf(b,32,L"%u",s.pid);return std::wstring(b);
                default:return std::wstring(SvcStateName(s.state));}},
        g.svcScroll,&g_lay.svcScroll);
}

static void DrawStartup(){
    int x0=g_lay.content.left+Si(8),y0=ContentTop()+Si(8);
    int W=g_lay.content.right-g_lay.content.left-Si(16),H=ContentBottom()-y0-Si(8);
    static const wchar_t* cols[]={L"Nome",L"Comando",L"Local",L"Estado"};
    float ws[]={220,420,160,110};
    std::vector<std::wstring> colsv(cols,cols+4);
    DrawSimpleGrid(x0,y0,W-Si(14),H,colsv,ws,4,
        [](){std::lock_guard<std::mutex> lk(g.dataMtx);return (int)g.starts.size();},
        [](int i,int c){std::lock_guard<std::mutex> lk(g.dataMtx);const StartRow&s=g.starts[i];
            switch(c){case 0:return s.name;case 1:return s.cmd;case 2:return s.location;
                default:return std::wstring(s.enabled?L"Habilitado":L"Desabilitado");}},
        g.startScroll,&g_lay.startScroll);
}

static void DrawUsers(){
    int x0=g_lay.content.left+Si(8),y0=ContentTop()+Si(8);
    int W=g_lay.content.right-g_lay.content.left-Si(16),H=ContentBottom()-y0-Si(8);
    // expande: cada user vira 1 + N linhas (filhos = processos)
    struct VRow{const UserRow*u;const ProcRow*p;};
    std::vector<VRow> rows; rows.reserve(1024);
    {
        std::lock_guard<std::mutex> lk(g.dataMtx);
        std::unordered_map<DWORD,const ProcRow*> byPid;
        for(auto&p:g.procs)byPid[p.pid]=&p;
        for(auto&u:g.users){rows.push_back({&u,nullptr});
            if(u.expanded)for(DWORD pid:u.pids){auto it=byPid.find(pid);if(it!=byPid.end())rows.push_back({&u,it->second});}}
    }
    int total=(int)rows.size();
    int headH=Si(24),rowH=Si(22),viewH=H-headH;
    int maxScroll=std::max(0,total*rowH-viewH);
    g.userScroll=ClampV(g.userScroll,0,maxScroll);
    FillR(g.dc,g.bHeader,RECT{x0,y0,x0+W-Si(14),y0+headH});
    LineH(g.dc,g.bBrdD,(float)x0,(float)(y0+headH),(float)(W-Si(14)));
    float cols[]={220,80,90,90,120};
    const wchar_t* cn[]={L"Usuário",L"Sessão",L"Processos",L"CPU",L"Memória"};
    float cx=(float)x0;for(int c=0;c<5;c++){float cw=cols[c]*S(1);DrawTxt(g.dc,g.fHead,cn[c],cx+Si(6),(float)y0,cw-Si(10),(float)headH,g.bDim);cx+=cw;LineV(g.dc,g.bBrd,cx,(float)y0,(float)headH);}
    int first=g.userScroll/rowH,last=std::min(total-1,(g.userScroll+viewH)/rowH);
    g.dc->PushAxisAlignedClip(D2D1::RectF((float)x0,(float)(y0+headH),(float)(x0+W-Si(14)),(float)(y0+H)),D2D1_ANTIALIAS_MODE_ALIASED);
    for(int i=first;i<=last&&i<total;i++){
        int ry=y0+headH+i*rowH-g.userScroll;
        const VRow&vr=rows[i];
        if(i%2==0)FillRf(g.dc,g.bWin,(float)x0,(float)ry,(float)(W-Si(14)),(float)rowH);
        float ccx=(float)x0;wchar_t b[64];
        if(!vr.p){ // linha de usuario
            std::wstring un=(vr.u->expanded?L"▼ ":L"▶ ")+vr.u->name;
            DrawTxt(g.dc,g.fBody,un.c_str(),ccx+Si(6),(float)ry,cols[0]*S(1)-Si(10),(float)rowH,g.bTxt);
            swprintf(b,64,L"%d",vr.u->session);DrawTxt(g.dc,g.fBody,b,ccx+cols[0]*S(1)+Si(6),(float)ry,cols[1]*S(1)-Si(10),(float)rowH,g.bDim);
            swprintf(b,64,L"%d",vr.u->procCount);DrawTxt(g.dc,g.fBody,b,ccx+(cols[0]+cols[1])*S(1)+Si(6),(float)ry,cols[2]*S(1)-Si(10),(float)rowH,g.bDim);
            swprintf(b,64,L"%.1f",vr.u->cpu);DrawTxt(g.dc,g.fBody,b,ccx+(cols[0]+cols[1]+cols[2])*S(1)+Si(6),(float)ry,cols[3]*S(1)-Si(10),(float)rowH,g.bDim);
            DrawTxt(g.dc,g.fBody,FormatBytes(vr.u->memMB*1048576.0).c_str(),ccx+(cols[0]+cols[1]+cols[2]+cols[3])*S(1)+Si(6),(float)ry,cols[4]*S(1)-Si(10),(float)rowH,g.bDim);
        } else { // processo filho
            DrawTxt(g.dc,g.fSmall,(L"    "+vr.p->name).c_str(),ccx+Si(16),(float)ry,cols[0]*S(1)-Si(16),(float)rowH,g.bDim);
            swprintf(b,64,L"%u",vr.p->pid);DrawTxt(g.dc,g.fSmall,b,ccx+cols[0]*S(1)+Si(6),(float)ry,cols[1]*S(1)-Si(10),(float)rowH,g.bDim);
            swprintf(b,64,L"%.1f",vr.p->cpuPct);DrawTxt(g.dc,g.fSmall,b,ccx+(cols[0]+cols[1])*S(1)+Si(6),(float)ry,(cols[2]+cols[3]+cols[4])*S(1)-Si(10),(float)rowH,g.bDim);
        }
        LineH(g.dc,g.bBrd,(float)x0,(float)(ry+rowH),(float)(W-Si(14)));
        ccx+=0;
    }
    g.dc->PopAxisAlignedClip();
    FrameRf(g.dc,g.bBrd,(float)x0,(float)y0,(float)(W-Si(14)),(float)H,1);
    // vscroll
    g_lay.userScroll={x0+W-Si(14),y0+headH,x0+W,y0+viewH};
    FillR(g.dc,g.bCtl,g_lay.userScroll);FrameR(g.dc,g.bBrd,g_lay.userScroll,1);
    if(total*rowH>viewH&&viewH>0){float th=std::max((float)Si(20),(float)viewH*viewH/(float)(total*rowH));
        float ty=(float)(y0+headH)+((float)g.userScroll/std::max(1,maxScroll))*((float)viewH-th);
        FillRf(g.dc,g.bBrdD,(float)(x0+W-Si(12)),ty,(float)(Si(12)-2),th);}
}

// ---------------------------------------------------------------- frame principal
void RenderFrame()
{
    if (!g.dc || !g.swap) return;
    EnsureBrushes();
    g.dc->BeginDraw();
    g.dc->Clear(Skin::Win);

    // desenha chrome primeiro (preenche titleBar/tabStrip/statusBar)
    DrawChrome();
    DrawStatusBar();
    g_lay.content={Si(6),g_lay.tabStrip.bottom, g.pxW-Si(6), g_lay.statusBar.top};

    switch(g.tab){
        case Tab::Processes: DrawProcesses(); break;
        case Tab::Performance: DrawPerformance(); break;
        case Tab::Services: DrawServices(); break;
        case Tab::Startup: DrawStartup(); break;
        case Tab::Users: DrawUsers(); break;
    }
    g.dc->EndDraw();
    g.swap->Present(0,0);
}
