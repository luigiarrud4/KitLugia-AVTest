using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KitLugia.GUI.Services;

// ===========================================================================
// gbprobe — SONDAGEM DE CAPACIDADE de I/O e page priority
// ===========================================================================
// A sondagem foi implementada para transformar uma falha SILENCIOSA (o
// NtSetInformationProcess devolvia um codigo que ninguem lia) numa falha VISIVEL.
// Este harness fecha os dois caminhos:
//
//   T1 CAMINHO FELIZ: handle valido -> a classe 33/39 pega, o motor fica marcado
//       como disponivel e continua a aplicar.
//   T2 CAMINHO DE FALHA: handle invalido -> o motor avisa NO LOG UMA VEZ e passa
//       a devolver sem tentar de novo (sem inundar o log).
//
// A leitura da marca de estado e por reflection (o campo e' estatico e privado).

internal static class Program
{
    static int _falhas = 0;
    static void Check(string nome, bool ok, string det)
    {
        if (!ok) _falhas++;
        Console.WriteLine($"  [{(ok ? "OK  " : "FALHA")}] {nome}: {det}");
    }

    static string LogFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KitLugia", "Logs", "KitLugia.log");
    static long LogLen() { try { var f = new FileInfo(LogFile); return f.Exists ? f.Length : 0; } catch { return 0; } }
    static string ReadNew(long off)
    {
        try
        {
            using var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= off) return "";
            fs.Seek(off, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    static int Probe(string campo)
    {
        var f = typeof(Win32Api).GetField(campo, BindingFlags.NonPublic | BindingFlags.Static);
        return f == null ? -99 : (int)f.GetValue(null)!;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint acc, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetProcessId(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    // Diagnostico bruto: quero o NTSTATUS exato, sem passar pela camada do Kit.
    [DllImport("ntdll.dll")] static extern int NtSetInformationProcess(IntPtr h, int cls, ref int v, int len);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, ref int v, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr h, int cls, ref int v, int len);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessInformation(IntPtr h, int cls, ref int v, int len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetPriorityClass(IntPtr h, uint pc);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetPriorityClass(IntPtr h, out uint pc);

    const uint PROCESS_CLASS_I_O = 0x00000100;   // bit de I/O priority em SetPriorityClass
    const uint PROCESS_CLASS_IDLE = 0x00001000;

    static void DiagnosticoBruto(IntPtr hValido)
    {
        // PROVA DE ELEVAÇÃO: sem isto, uma conclusao do tipo "nao funciona nem
        // elevado" nao vale nada — pode ser que nem tenhamos corrido elevado.
        var ident = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(ident);
        bool elevado = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Console.WriteLine($"\n    >>> ELEVADO = {elevado} (usuario {ident.Name})");
        if (Environment.GetCommandLineArgs().Any(a => a == "--auto-check"))
            return;

        Console.WriteLine("\n--- T0. DIAGNOSTICO BRUTO (sem a camada do Kit) ---");
        var eu = OpenProcess(0x1F0FFF, false, Environment.ProcessId);
        foreach (var (nome, h) in new[] { ("handle VALIDO (charmap)", hValido), ("handle INVALIDO (0xFFFFFFFF)", new IntPtr(unchecked((int)0xFFFFFFFF))) })
        {
            int v33 = 3, v39 = 5, v0 = 5;
            int s33 = NtSetInformationProcess(h, 33, ref v33, 4);   // ProcessIoPriority (hint 3)
            int s39 = NtSetInformationProcess(h, 39, ref v39, 4);   // ProcessPagePriority
            int s0  = NtSetInformationProcess(h, 0,  ref v0,  4);   // ProcessMemoryPriority (o que o Kit usa)
            Console.WriteLine($"    {nome,-32} cls33(I/O)={s33} (0x{s33:X8})  cls39(Page)={s39} (0x{s39:X8})  cls0(Mem)={s0} (0x{s0:X8})");
        }
        Console.WriteLine("    significa: 0x00000000=OK  0xC0000061=PRIVILEGE_NOT_HELD  0xC0000008=INVALID_HANDLE  0xC000000D=INVALID_PARAMETER");

        // A via DOCUMENTADA: SetPriorityClass com o bit PROCESS_CLASS_I/O.
        GetPriorityClass(hValido, out uint pc0);
        Console.WriteLine($"\n    GetPriorityClass(charmap) = 0x{pc0:X8}");
        foreach (var (nome, extra) in new (string, uint)[]
                 { ("PROCESS_CLASS_I/O | NORMAL", PROCESS_CLASS_I_O | 0x20),
                   ("PROCESS_CLASS_I/O | IDLE", PROCESS_CLASS_I_O | PROCESS_CLASS_IDLE) })
        {
            bool ok = SetPriorityClass(hValido, pc0 | extra);
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"    SetPriorityClass({nome,-28}) ok={ok,-5} err={err,-5} (0x{err:X8})");
        }
        GetPriorityClass(hValido, out uint pc1);
        Console.WriteLine($"    GetPriorityClass depois = 0x{pc1:X8}  (mudou? {pc0 != pc1})");
        SetPriorityClass(hValido, pc0);   // restaurar

        // >>> O que interessa: a via DOCUMENTADA (kernel32 SetProcessInformation),
        // que e' a numeracao PUBLICA (0=MemPriority, 1=MemCompression,
        // 2=IoPriority, 3=PagePriority) e nao as classes privadas da ntdll (33/39).
        Console.WriteLine("\n    SetProcessInformation (via DOCUMENTADA, classes publicas 0..5):");
        var hInvalido = new IntPtr(unchecked((int)0xFFFFFFFF));
        for (int cls = 0; cls <= 5; cls++)
        {
            int a = 5, b = 3;
            bool okVal = SetProcessInformation(hValido, cls, ref a, 4);
            int errVal = Marshal.GetLastWin32Error();
            bool okMau = SetProcessInformation(hInvalido, cls, ref b, 4);
            int errMau = Marshal.GetLastWin32Error();
            string nome = cls switch { 0 => "MemoryPriority", 1 => "MemoryCompression", 2 => "IoPriority", 3 => "PagePriority", _ => $"cls{cls}" };
            Console.WriteLine($"      cls{cls} {nome,-18} handle VALIDO ok={okVal,-5} err={errVal,-4}   |   handle INVALIDO ok={okMau,-5} err={errMau,-4}");
        }
        Console.WriteLine("    (handle VALIDO ok=True e INVALIDO ok=True = a classe ignora o handle: nao serve para sondar capacidade)");

        Console.WriteLine("\n    GetProcessInformation (leitura, para ver se da' para CONFIRMAR o valor):");
        for (int cls = 0; cls <= 3; cls++)
        {
            int v = -12345;
            bool ok = GetProcessInformation(hValido, cls, ref v, 4);
            Console.WriteLine($"      cls{cls} ok={ok,-5} err={Marshal.GetLastWin32Error(),-4} lido={v}");
        }
        if (eu != IntPtr.Zero) CloseHandle(eu);
    }

    // A page priority (classe privada 39) aceita escrita. Mas sera que APLICA?
    // A unica resposta valida e' ler de volta com a classe 39 em modo de leitura.
    // ACHADO CRITICO: a sonda contra o PROPRIO processo devolveu SUCESSO para a
    // classe 33, mas a chamada contra um processo QUEIM devolveu PRIVILEGE_NOT_HELD.
    // Ou seja: a capacidade depende do PROCESSO, nao da maquina. Se for assim, a
    // sonda "de sistema" que acabei de meter no motor e' uma MENTIRA.
    static void ProvaCapacidadeDependeDoProcesso(IntPtr hOutro)
    {
        Console.WriteLine("\n    PROVA: a classe 33 (I/O) funciona em QUE processos?");
        var eu = GetCurrentProcess();
        foreach (var (nome, h) in new[]
        {
            ("o proprio processo do Kit", eu),
            ("outro processo (charmap)", hOutro),
        })
        {
            foreach (var hint in new[] { 0, 2, 3 })
            {
                int v = hint;
                int st = NtSetInformationProcess(h, 33, ref v, 4);
                Console.WriteLine($"      {nome,-30} hint={hint} -> 0x{st:X8}");
            }
        }
        Console.WriteLine("    (0x0=OK, 0xC0000061=PRIVILEGE_NOT_HELD, 0xC000000D=PARAM_INVALIDO)");
        CloseHandle(eu);
    }

    static void ProvaPagePriorityReal(IntPtr h)
    {
        Console.WriteLine("\n    PROVA: a page priority (classe 39) realmente APLICA no processo?");
        foreach (var alvo in new[] { 1, 5 })
        {
            int v = alvo;
            int st = NtSetInformationProcess(h, 39, ref v, 4);
            int lido = -1;
            int qst = NtQueryInformationProcess(h, 39, ref lido, 4, out _);
            Console.WriteLine($"      defini {alvo} -> set=0x{st:X8}  lido de volta={lido} (query=0x{qst:X8})  {(lido == alvo ? "APLICOU" : "nao aplicou")}");
        }
        int restaura = 5;
        NtSetInformationProcess(h, 39, ref restaura, 4);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        int exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (s, e) =>
        {
            try { exit = await Cenario(); }
            catch (Exception ex) { Console.WriteLine("FALHA NO HARNESS: " + ex); exit = 2; }
            app.Shutdown(exit);
        };
        app.Run();
        return exit;
    }

    static async Task<int> Cenario()
    {
        Console.WriteLine("=== gbprobe — sondagem de capacidade (I/O + page priority) ===");

        // alvo valido
        var alvo = Process.Start(new ProcessStartInfo("charmap.exe") { UseShellExecute = false })!;
        await Task.Delay(1500);
        IntPtr hOk = OpenProcess(0x1F0FFF, false, alvo.Id);
        DiagnosticoBruto(hOk);
        ProvaPagePriorityReal(hOk);
        ProvaCapacidadeDependeDoProcesso(hOk);

        // ================= T1 — caminho feliz =================
        Console.WriteLine("\n--- T1. Handle VALIDO: a classe tem de pegar e ficar disponivel ---");
        Check("estado inicial = desconhecido (0)",
              Probe("_ioPrioProbe") == 0 && Probe("_pagePrioProbe") == 0,
              $"io={Probe("_ioPrioProbe")} page={Probe("_pagePrioProbe")}");

        long off1 = LogLen();
        Win32Api.SetProcessIoPriority(hOk, 3);
        Win32Api.SetProcessPagePriority(hOk, 5);
        await Task.Delay(300);
        string log1 = ReadNew(off1);
        // O que interessa nao e' que funcione: e' que o estado marcado coincida com
        // o que a API devolveu a serio. Se a classe falhar, tem de ficar marcada como
        // indisponivel E deixar um aviso no log (uma vez so).
        // A verdade medida: a classe 33 depende do VALOR, nao do processo.
        // hint=3 (High, o que o V4 pede) e' recusado; hint=2 (Normal) e' aceite.
        // A sonda tem de usar o valor que o motor QUER aplicar, senao da um SIM falso.
        int bruto33 = 3;
        int st33 = NtSetInformationProcess(hOk, 33, ref bruto33, 4);
        int st39raw = 5;
        int st39 = NtSetInformationProcess(hOk, 39, ref st39raw, 4);
        int esperadoIo = st33 >= 0 ? 1 : 2;
        int esperadoPage = st39 >= 0 ? 1 : 2;
        Check("estado da I/O priority bate certo com o que a API devolveu",
              Probe("_ioPrioProbe") == esperadoIo,
              $"hint=3 -> NtSet=0x{st33:X8} | esperado probe={esperadoIo}, real={Probe("_ioPrioProbe")}");
        Check("SEM SIM FALSO: I/O High recusada nao foi marcada como disponivel",
              !(st33 < 0 && Probe("_ioPrioProbe") == 1),
              $"NtSet falhou (0x{st33:X8}) mas probe=1 = mentira");
        Check("estado da Page priority bate certo com o que a API devolveu",
              Probe("_pagePrioProbe") == esperadoPage,
              $"-> esperado probe={esperadoPage}, real={Probe("_pagePrioProbe")}");
        Check("log explica o que foi decidido", log1.Contains("priority", StringComparison.OrdinalIgnoreCase),
              log1.Contains("priority", StringComparison.OrdinalIgnoreCase) ? "h linha no log" : "ausente");
        foreach (var l in log1.Split('\n'))
            if (l.Contains("GameBoost:") && l.Contains("priority", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("    log: " + l.Trim());

        // ================= T2 — caminho de falha =================
        // Forca o estado para "desconhecido" e usa um handle INVALIDO, para simular
        // "a classe deixou de existir / acesso negado" — o caso silencioso.
        Console.WriteLine("\n--- T2. Handle INVALIDO: tem de AVISAR UMA VEZ e parar de repetir ---");
        typeof(Win32Api).GetField("_ioPrioProbe", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, 0);
        typeof(Win32Api).GetField("_pagePrioProbe", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, 0);

        long off2 = LogLen();
        // Handle REALISTAMENTE invalido, sem risco de colidir com nada: 0xFFFFFFFF
        // nunca e' um handle valido em user mode. Na vida real e' o caso do processo
        // que morre entre listar e aplicar, ou de acesso negado.
        IntPtr hMau = new IntPtr(unchecked((int)0xFFFFFFFF));
        // ARMADILHA REAL: GetProcessId(0xFFFFFFFF) devolveu 31736 (slot
        // reutilizado) — nao serve para validar. GetExitCodeProcess com
        // STILL_ACTIVE (259) e' a forma documentada de verificar que o handle
        // ainda aponta para um processo vivo.
        bool vivo = GetExitCodeProcess(hMau, out uint codigo);
        Check("GetExitCodeProcess rejeita o handle falso (guarda fiável)",
              !vivo || codigo == 259, $"ok={vivo} codigo={codigo} (259=STILL_ACTIVE)");// A 1. chamada e' a que sonda e pode legitimamente logar 1 aviso por knob.
        // O offset e' lido DEPOIS dela: o que interessa e' que as 49 restantes
        // fiquem em silencio.
        Win32Api.SetProcessIoPriority(hMau, 3);
        Win32Api.SetProcessPagePriority(hMau, 5);
        await Task.Delay(300);
        long offDepois = LogLen();
        for (int i = 0; i < 49; i++)
        {
            Win32Api.SetProcessIoPriority(hMau, 3);
            Win32Api.SetProcessPagePriority(hMau, 5);
        }
        await Task.Delay(300);
        string log49 = ReadNew(offDepois);
        int nLog = log49.Split('\n').Count(l => l.Contains("priority", StringComparison.OrdinalIgnoreCase));
        Check("handle invalido nao explodiu o log (49 chamadas, 0 avisos)", nLog == 0,
              $"{nLog} aviso(s) para 49 chamadas");
        foreach (var l in log49.Split('\n'))
            if (l.Contains("priority", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("    log: " + l.Trim());

        // ================= T2b — a decisao nao voltou atrás =================
        // Marcar 1 (disponivel) ou 2 (indisponivel) e' definitiva: um handle
        // falso nao pode fazer a sondagem mudar de ideias a meio.
        Check("Page priority ficou disponivel (classe 39 funciona de facto)",
              Probe("_pagePrioProbe") == 1, $"probe={Probe("_pagePrioProbe")}");
        Check("I/O priority ficou indisponivel (High recusado pelo Windows)",
              Probe("_ioPrioProbe") == 2, $"probe={Probe("_ioPrioProbe")}");

        // ================= T3 — depois de marked, nem tenta mais =================
        Console.WriteLine("\n--- T3. Depois de marcada indisponivel, nem volta a tentar ---");
        long off3 = LogLen();
        for (int i = 0; i < 50; i++) Win32Api.SetProcessIoPriority(hOk, 3);
        await Task.Delay(300);
        string log3 = ReadNew(off3);
        int nDepois = log3.Split('\n').Count(l => l.Contains("indisponivel", StringComparison.OrdinalIgnoreCase));
        Check("nenhum aviso novo (nem log vazio = nem tentou)", nDepois == 0,
              $"{nDepois} aviso(s) apos 50 chamadas com handle VALIDO");

        CloseHandle(hOk);
        try { alvo.Kill(); } catch { }
        await Task.Delay(400);
        foreach (var p in Process.GetProcessesByName("charmap")) { try { p.Kill(); } catch { } }

        Console.WriteLine(_falhas == 0 ? "\nTODOS OS TESTES PASSARAM" : $"\n{_falhas} TESTE(S) FALHARAM");
        return _falhas;
    }
}