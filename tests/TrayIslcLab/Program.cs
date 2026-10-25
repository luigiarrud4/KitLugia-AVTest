// TrayIslcLab - exercita o CheckAndCleanStandby do TrayIconService de verdade.
//
// POR QUE NAO CHAMAR Initialize(): ele sobe NotifyIcon, watchdog, LoadSettings,
// GameBoost e auto-start. Nao e o que queremos testar e tem efeitos colaterais.
// O construtor e trivial (so cria o DispatcherTimer, sem Start), entao
// `new TrayIconService()` + reflexao no metodo privado isola exatamente o
// codigo do purge.
//
// SEGURANCA (regra do AGENTS.md, depois do incidente em que um harness apagou
// os limites de RAM do usuario):
//   - o cenario padrao NAO chama SaveSettings em momento algum (so leitura);
//   - as preferencias sao mudadas em memoria e restauradas no fim;
//   - o backup do registry + JSON + log roda em TODO caso, e o restore e o
//     ultimo passo mesmo em caso de excecao.
//   Use --phase2 para exercitar a persistencia (ai sim grava), que exige o backup.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using KitLugia.GUI.Services;

class Lab
{
    const string RegKey = @"HKCU\Software\KitLugia";
    const string LocalApp = @"%LOCALAPPDATA%\KitLugia";

    static int _fail, _ok;
    static TrayIslcLog _log = new();

    static void Check(string what, bool cond, string detail = "")
    {
        if (cond) { _ok++; _log.Ok(what, detail); }
        else { _fail++; _log.Fail(what, detail); }
    }

    [STAThread]
    static int Main(string[] argv)
    {
        bool phase2 = argv.Contains("--phase2");
        _log.Init();

        Console.WriteLine("TrayIslcLab - exercita o purge de standby do TrayIconService");
        Console.WriteLine(new string('=', 66));
        Console.WriteLine($"elevado: {IsAdmin()}   (o purge exige SeProfileSingleProcessPrivilege)");

        // ---------- 1. BACKUP (obrigatorio, mesmo no cenario read-only) ----------
        string backupDir = Path.Combine(Path.GetTempPath(), "trayislclab_backup_" +
            DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(backupDir);
        _log.Line($"backup em: {backupDir}");
        int rc = Run("reg.exe", $"export \"{RegKey}\" \"{Path.Combine(backupDir, "reg.rive")}\" /y");
        _log.Line($"reg export: exit={rc}" + (rc != 0 ? " (chave pode nao existir - ok)" : ""));

        var jsonBackup = new List<(string orig, string bak)>();
        try
        {
            var la = Environment.ExpandEnvironmentVariables(LocalApp);
            if (Directory.Exists(la))
            {
                foreach (var f in Directory.GetFiles(la, "*.json", SearchOption.AllDirectories))
                {
                    var b = Path.Combine(backupDir, Path.GetFileName(f));
                    File.Copy(f, b, true);
                    jsonBackup.Add((f, b));
                }
            }
            // O LogStore trunca o log no primeiro uso - guarda tambem.
            var logDir = Path.Combine(la, "Logs");
            if (Directory.Exists(logDir))
                foreach (var f in Directory.GetFiles(logDir, "*"))
                {
                    var b = Path.Combine(backupDir, Path.GetFileName(f) + ".bak");
                    File.Copy(f, b, true);
                    jsonBackup.Add((f, b));
                }
            _log.Line($"backup: {jsonBackup.Count} arquivo(s) + registro");
        }
        catch (Exception ex) { _log.Line("falha no backup de arquivos: " + ex.Message); }

        int exit = 1;
        try
        {
            exit = RunScenarios(phase2);
        }
        catch (Exception ex)
        {
            _log.Line("EXCECAO: " + ex);
            exit = 1;
        }
        finally
        {
            // ---------- RESTORE (sempre, mesmo com excecao) ----------
            Restore(backupDir, jsonBackup);
        }

        Console.WriteLine(new string('=', 66));
        Console.WriteLine($"RESULTADO: {_ok} OK / {_fail} FALHA");
        Console.WriteLine($"log completo: {_log.Path}");
        Environment.Exit(_fail == 0 ? 0 : 1);
        return _fail == 0 ? 0 : 1;
    }

    static int RunScenarios(bool phase2)
    {
        var svc = new TrayIconService(); // ctor trivial: sem NotifyIcon, sem LoadSettings
        var t = svc.GetType();

        var mCheck = t.GetMethod("CheckAndCleanStandby", BindingFlags.NonPublic | BindingFlags.Instance);
        Check("CheckAndCleanStandby encontrado via reflexao", mCheck != null);
        if (mCheck == null) return 1;

        Func<int, object> inv = v => mCheck.Invoke(svc, new object[] { v });

        // Guarda os valores originais para restaurar em memoria.
        var orig = new Dictionary<string, object>
        {
            ["IslcThresholdMB"] = svc.IslcThresholdMB,
            ["IslcFreeThresholdMB"] = svc.IslcFreeThresholdMB,
            ["IslcMinIntervalSeconds"] = svc.IslcMinIntervalSeconds,
            ["IslcRespectForeground"] = svc.IslcRespectForeground,
            ["IslcCleanCount"] = svc.IslcCleanCount,
            ["IslcLastCleanTime"] = svc.IslcLastCleanTime,
            ["IslcLastSkipReason"] = svc.IslcLastSkipReason,
        };
        Action restore = () =>
        {
            svc.IslcThresholdMB = (long)orig["IslcThresholdMB"];
            svc.IslcFreeThresholdMB = (long)orig["IslcFreeThresholdMB"];
            svc.IslcMinIntervalSeconds = (int)orig["IslcMinIntervalSeconds"];
            svc.IslcRespectForeground = (bool)orig["IslcRespectForeground"];
            svc.IslcCleanCount = (int)orig["IslcCleanCount"];
            svc.IslcLastCleanTime = (DateTime)orig["IslcLastCleanTime"];
            svc.IslcLastSkipReason = (string)orig["IslcLastSkipReason"];
        };

        try
        {
            long standby = ReadStandby(), free = ReadFree();
            Console.WriteLine($"\nEstado real: standby={standby} MB  free={free} MB\n");
            _log.Line($"standby={standby} MB  free={free} MB");

            // ---- S1: standby abaixo do limiar -> nao age ----
            svc.IslcThresholdMB = 999999;          // standby real nunca chega aqui
            svc.IslcFreeThresholdMB = 0;           // free < 0 nunca e verdade
            svc.IslcMinIntervalSeconds = 0;
            svc.IslcRespectForeground = false;
            svc.IslcCleanCount = 0;
            svc.IslcLastSkipReason = "";
            inv(50);
            Check("S1 standby<limiar: nao age", svc.IslcCleanCount == 0,
                  $"count={svc.IslcCleanCount} motivo='{svc.IslcLastSkipReason}'");

            // ---- S2: standby passa, mas RAM sobrando -> o gate segura (G1) ----
            // O limiar vai a 0 para que a standby REAL sempre passe, senao o teste
            // depende de o cache estar acima de 64 MB naquele instante (a standby
            // se repovoa o tempo todo) e falhava por razao externa.
            svc.IslcThresholdMB = 0;                // standby real sempre >= 0
            svc.IslcFreeThresholdMB = 0;            // free >= 0 e sempre verdade
            svc.IslcCleanCount = 0;
            svc.IslcLastSkipReason = "";
            inv(50);
            Check("S2 gate de RAM livre segura (G1)", svc.IslcCleanCount == 0,
                  $"free={free} MB, limite=0 -> motivo='{svc.IslcLastSkipReason}'");
            Check("S2 o motivo cita a memoria livre",
                  svc.IslcLastSkipReason.Contains("memória livre"),
                  $"'{svc.IslcLastSkipReason}'");

            // ---- S3: os dois criterios satisfeitos -> PURGA de verdade ----
            long antes = ReadStandby();
            svc.IslcThresholdMB = 0;               // standby sempre >= 0
            svc.IslcFreeThresholdMB = 999999999;   // free sempre < limite
            svc.IslcMinIntervalSeconds = 0;
            svc.IslcCleanCount = 0;
            svc.IslcLastSkipReason = "";
            svc.IslcLastCleanTime = DateTime.MinValue;
            inv(50);
            WaitPurge(svc);
            long depois = ReadStandby();
            _log.Line($"S3 standby {antes} -> {depois} MB, ntStatus=0x{svc.IslcLastNtStatus:X8}");

            // FATO QUE O TESTE APRENDEU: a standby list NAO e monotonica. Depois de um
            // purge OK (ntStatus=0) o proprio Windows volta a popular a lista com as
            // paginas que os apps ativos voltaram a tocar - medir de fora pode dar
            // MAIOR que o "antes" (88 -> 141 MB observados). Por isso o teste nao
            // exige `depois < antes` com a leitura propria: quem mede no instante
            // certo e o servico (TryGetStandbyListSizeMB logo apos o purge), e ele
            // expoe o resultado em IslcCleanCount / IslcLastCleanMB.
            Check("S3 purge executou e mediu queda (medida interna do servico)",
                  svc.IslcCleanCount == 1,
                  $"{antes} -> {depois} MB lidos pelo harness (standby se repovoa); " +
                  $"count={svc.IslcCleanCount}, ntStatus=0x{svc.IslcLastNtStatus:X8}");
            Check("S3 NTSTATUS registrado", svc.IslcLastNtStatus == 0,
                  $"0x{svc.IslcLastNtStatus:X8} (0 = ok)");
            if (svc.IslcCleanCount == 1)
            {
                // IslcLastCleanMB e a leitura do servico no instante do purge.
                Check("S3 IslcLastCleanMB abaixo do 'antes' do harness",
                      svc.IslcLastCleanMB <= antes,
                      $"IslcLastCleanMB={svc.IslcLastCleanMB} <= antes={antes}");
            }
            else
            {
                Console.WriteLine($"        (purge nao houve queda mensuravel - " +
                                  $"motivo='{svc.IslcLastSkipReason}')");
                Check("S3 sem queda: motivo explica",
                      !string.IsNullOrWhiteSpace(svc.IslcLastSkipReason),
                      $"motivo='{svc.IslcLastSkipReason}'");
            }

            // ---- S4: intervalo minimo segura o segundo purge ----
            svc.IslcMinIntervalSeconds = 3600;
            svc.IslcCleanCount = 0;
            svc.IslcLastSkipReason = "";
            inv(50);
            Check("S4 intervalo minimo segura", svc.IslcCleanCount == 0,
                  $"motivo='{svc.IslcLastSkipReason}'");
            Check("S4 o motivo cita o intervalo",
                  svc.IslcLastSkipReason.Contains("intervalo"), $"'{svc.IslcLastSkipReason}'");

            // ---- S5: foreground ----
            svc.IslcMinIntervalSeconds = 0;
            svc.IslcRespectForeground = true;
            bool temFoco = GetForegroundWindow() != IntPtr.Zero;
            svc.IslcCleanCount = 0;
            svc.IslcLastSkipReason = "";
            inv(50);
            Check("S5 foreground bloqueia", !temFoco || svc.IslcCleanCount == 0,
                  $"foco={temFoco}, count={svc.IslcCleanCount}, motivo='{svc.IslcLastSkipReason}'");

            // ---- S6: guarda de reentrancia (G10) ----
            // IslcRespectForeground = false de proposito: com foreground ligado o
            // filtro de foreground dispara ANTES da guarda e o teste passaria sem
            // exercitar a guarda (foi exatamente o que aconteceu na 1a rodada).
            svc.IslcRespectForeground = false;
            var fPurging = t.GetField("_isPurgingStandby", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fPurging != null)
            {
                svc.IslcThresholdMB = 0;
                svc.IslcFreeThresholdMB = 999999999;
                svc.IslcMinIntervalSeconds = 0;
                fPurging.SetValue(svc, 1); // simula um purge em voo
                svc.IslcCleanCount = 0;
                svc.IslcLastSkipReason = "";
                inv(50);
                Check("S6 guarda de reentrancia segura", svc.IslcCleanCount == 0,
                      $"motivo='{svc.IslcLastSkipReason}'");
                Check("S6 o motivo e o da guarda (nao de outro filtro)",
                      svc.IslcLastSkipReason.Contains("andamento"),
                      $"'{svc.IslcLastSkipReason}'");
                Check("S6 IsPurgeStandbyInFlight reflete o estado",
                      svc.IsPurgeStandbyInFlight, "true enquanto o flag esta ligado");
                fPurging.SetValue(svc, 0);

                // Soltar a guarda precisa devolver o purge a funcionar (o bug que
                // o harness pegou: a guarda ficava presa em 1 para sempre).
                // Nao se pode exigir IslcCleanCount aqui: a standby ja foi a 0
                // no S3, e o contador (por projeto) sobe so quando o valor
                // CAI de verdade. O que se prova e que a execucao chegou
                // em TryPurgeStandbyList - via um sentinela em IslcLastNtStatus.
                svc.IslcLastNtStatus = -99; // valor impossivel: nao sai da API
                svc.IslcLastSkipReason = "";
                svc.IslcCleanCount = 0;
                inv(50);
                WaitPurge(svc);
                Check("S6 apos soltar a guarda, o purge volta a ser executado",
                      svc.IslcLastNtStatus != -99 && svc.IslcLastSkipReason != "purge anterior em andamento",
                      $"ntStatus=0x{svc.IslcLastNtStatus:X8} (sentinela -99 foi substituido = a task rodou), " +
                      $"motivo='{svc.IslcLastSkipReason}'");
            }
            else Check("campo _isPurgingStandby encontrado", false, "nao existe");

            // ---- S7: foreground desligado deixa passar (controle) ----
            svc.IslcRespectForeground = false;
            svc.IslcThresholdMB = 999999; // nao age de novo
            svc.IslcCleanCount = 0;
            svc.IslcLastSkipReason = "";
            inv(50);
            Check("S7 com limiar alto continua nao agindo", svc.IslcCleanCount == 0,
                  $"count={svc.IslcCleanCount}");

            // ---- FASE 2 (opcional): persistencia no registry ----
            if (phase2)
            {
                Console.WriteLine("\n-- fase 2: persistencia --");
                svc.IslcThresholdMB = 1234;
                svc.IslcFreeThresholdMB = 777;
                svc.IslcMinIntervalSeconds = 45;
                svc.IslcRespectForeground = false;
                svc.SaveSettings();

                var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\KitLugia\TraySettings");
                long g1 = Convert.ToInt64(k.GetValue("IslcThresholdMB", 0L));
                long g2 = Convert.ToInt64(k.GetValue("IslcFreeThresholdMB", 0L));
                int g3 = Convert.ToInt32(k.GetValue("IslcMinIntervalSeconds", 0));
                int g4 = Convert.ToInt32(k.GetValue("IslcRespectForeground", -1));
                k.Close();

                Check("F2 IslcThresholdMB persistido", g1 == 1234, $"{g1} (esperado 1234)");
                Check("F2 IslcFreeThresholdMB persistido", g2 == 777, $"{g2} (esperado 777)");
                Check("F2 IslcMinIntervalSeconds persistido", g3 == 45, $"{g3} (esperado 45)");
                Check("F2 IslcRespectForeground persistido (0=off)", g4 == 0, $"{g4} (esperado 0)");
            }
        }
        finally
        {
            restore();
        }
        return 0;
    }

    static void WaitPurge(TrayIconService svc)
    {
        // O purge roda em Task.Run. Dois tempos distintos, nesta ordem:
        //   1) esperar o flagficar 1 (a task realmente subiu);
        //   2) esperar o flag voltar a 0 (terminou).
        // Antes dormia 50 ms e checava direto: se a task ainda nao tinha
        // iniciado, IsPurgeStandbyInFlight era false e o helper voltava na hora,
        // liamos IslcLastCleanMB ANTES de ele ser escrito -> flake. O sintoma era
        // "IslcLastCleanMB=0 vs medido 4" em uma de cada tres execucoes.
        const int stepMs = 25, maxSteps = 400; // 10 s no total

        bool subiu = false;
        for (int i = 0; i < maxSteps && !subiu; i++)
        {
            System.Threading.Thread.Sleep(stepMs);
            if (svc.IsPurgeStandbyInFlight) subiu = true;
        }
        if (!subiu) return; // nenhum purge foi despachado (filtro barrou)

        for (int i = 0; i < maxSteps; i++)
        {
            System.Threading.Thread.Sleep(stepMs);
            if (!svc.IsPurgeStandbyInFlight) return;
        }
    }

    static void Restore(string backupDir, List<(string orig, string bak)> files)
    {
        Console.WriteLine("\nRESTAURANDO...");
        // registro
        var r = Path.Combine(backupDir, "reg.rive");
        if (File.Exists(r))
        {
            int rc = Run("reg.exe", $"delete \"{RegKey}\" /f");
            int rc2 = Run("reg.exe", $"import \"{r}\"");
            Console.WriteLine($"  registro: delete={rc} import={rc2}");
        }
        else Console.WriteLine("  registro: sem backup (chave nao existia)");

        foreach (var (orig, bak) in files)
        {
            try { if (File.Exists(bak)) File.Copy(bak, orig, true); }
            catch (Exception ex) { Console.WriteLine($"  falhou {orig}: {ex.Message}"); }
        }
        Console.WriteLine($"  {files.Count} arquivo(s) restaurado(s)");
        Console.WriteLine($"  (backup mantido em {backupDir} por inspecao)");
    }

    // ---- leituras diretas (mesma metrica do Core) ----
    [StructLayout(LayoutKind.Sequential)]
    struct MLI
    {
        public IntPtr Zero, Free, Mod, ModNW, Bad, P0, P1, P2, P3, P4, P5, P6, P7,
                     R0, R1, R2, R3, R4, R5, R6, R7, ModPF;
    }
    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int c, IntPtr p, int l, out int r);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    static MLI Read()
    {
        int sz = Marshal.SizeOf<MLI>();
        IntPtr b = Marshal.AllocHGlobal(sz);
        try
        {
            if (NtQuerySystemInformation(80, b, sz, out _) != 0) return default;
            return Marshal.PtrToStructure<MLI>(b);
        }
        finally { Marshal.FreeHGlobal(b); }
    }
    static long ReadStandby()
    {
        var i = Read();
        return (i.P0.ToInt64() + i.P1.ToInt64() + i.P2.ToInt64() + i.P3.ToInt64() +
                i.P4.ToInt64() + i.P5.ToInt64() + i.P6.ToInt64() + i.P7.ToInt64()) * 4 / 1024;
    }
    static long ReadFree() => (Read().Zero.ToInt64() + Read().Free.ToInt64()) * 4 / 1024;

    static int Run(string exe, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        { UseShellExecute = false, CreateNoWindow = true,
          RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = System.Diagnostics.Process.Start(psi);
        p.WaitForExit(30000);
        return p.ExitCode;
    }

    static bool IsAdmin()
    {
        var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Log do harness em arquivo, para eu poder ler depois.</summary>
class TrayIslcLog
{
    public string Path { get; private set; }
    StreamWriter _w;
    public void Init()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "trayislclab_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        _w = new StreamWriter(Path) { AutoFlush = true };
        Line("inicio " + DateTime.Now.ToString("s"));
    }
    public void Line(string s)
    {
        _w?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");
        Console.WriteLine("  . " + s);
    }
    public void Ok(string what, string d) => Line($"OK   {what}  ({d})");
    public void Fail(string what, string d) => Line($"FAIL {what}  ({d})");
}