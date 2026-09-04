using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;

namespace KitLugia.Core;

/// <summary>
/// Take Ownership de ARQUIVOS/PASTAS nativo — sem spawn de cmd/PowerShell.
/// Usa SeTakeOwnershipPrivilege via P/Invoke (mesma técnica do RegistryOwnership)
/// + .NET FileSystemSecurity para setar owner e permissões in-process.
/// Muito mais rápido que takeown.exe + icacls.exe (que abrem 2 processos por item).
///
/// PROVA-DE-TUDO (sessão 03/09):
/// - Probe nativo (GetFileAttributesW): distingue "não existe" de "existe mas acesso
///   negado" — o .NET Directory.Exists/File.Exists retorna FALSE em caminhos negados
///   (ex: C:\Windows.old inteiro é TrustedInstaller) e o Kit dizia "Caminho não encontrado".
/// - BFS pai→filho: dono da PASTA antes dos filhos — cada nível destrava o acesso do
///   próximo (árvores totalmente negadas funcionam).
/// - Skip de reparse points (junctions/symlinks): evita loop infinito em
///   C:\Windows.old\Documents and Settings → C:\Users.
/// - Arquivos/pastas negados usam SetNamedSecurityInfo (dono + substituição de DACL
///   por Administradores:F) — não precisa de permissão de LEITURA, só SeTakeOwnership.
/// - Fallback clássico takeown.exe + icacls (SID S-1-5-32-544, locale-proof) quando
///   restarem falhas — mesma técnica comprovada da Microsoft para Windows.old.
/// </summary>
public static class FileTakeOwnership
{
    // ───────── P/Invoke: privilégios ─────────
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    // SetNamedSecurityInfo direto — OWNER-only é O(1), sem tocar DACL (que dispara journal/Defender em pastas)
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int SetNamedSecurityInfo(string pObjectName, int ObjectType, int SecurityInfo,
        byte[]? psidOwner, byte[]? psidGroup, byte[]? pDacl, byte[]? pSacl);

    // Probe de existência nativo — distingue "não existe" de "existe mas ACL nega leitura"
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);

    // Probe de acesso REAL: abre o item para leitura. GetFileAttributesW NÃO detecta deny ACE
    // (passa mesmo em arquivo negado quando o processo é elevado/dono) — CreateFile é o teste
    // fiel: FILE_READ_DATA é o direito que o deny realmente bloqueia.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private const int SE_FILE_OBJECT = 1;
    private const int OWNER_SECURITY_INFORMATION = 0x00000001;
    private const int DACL_SECURITY_INFORMATION = 0x00000004;

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const int SE_PRIVILEGE_ENABLED = 0x0002;

    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_NOT_READY = 21;

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public long Luid;
        public int Attributes;
    }

    /// <summary>
    /// Abre o item para leitura e retorna false só quando a ACL realmente nega (erro 5/21).
    /// Erro 32 (compartilhamento por outro processo) NÃO é problema de ACL → retorna true
    /// (o takeown não precisa mexer no DACL por causa disso).
    /// </summary>
    private static bool CanReadOpen(string target)
    {
        try
        {
            IntPtr h = CreateFileW(target, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (h == new IntPtr(-1) || h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_ACCESS_DENIED || err == ERROR_NOT_READY) return false;
                return true; // erro 32/outros = não é ACL
            }
            CloseHandle(h);
            return true;
        }
        catch { return true; }
    }

    /// <summary>Habilita os privilégios necessários (chamado uma vez por operação).</summary>
    public static bool EnablePrivileges()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
                return false;
            try
            {
                foreach (var priv in new[] { "SeTakeOwnershipPrivilege", "SeRestorePrivilege", "SeBackupPrivilege" })
                {
                    if (!LookupPrivilegeValue(null, priv, out long luid)) continue;
                    var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                    AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
                return true;
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }

    /// <summary>
    /// Probe de existência à prova de ACL. O .NET Directory.Exists/File.Exists retorna
    /// FALSE quando o caminho existe mas a ACL nega leitura (ex: C:\Windows.old) — aqui
    /// distinguimos:
    ///   retorno 0  → existe (isDir correto)
    ///   retorno 5/21 → EXISTE mas acesso negado (isDir = true por segurança; dono pode
    ///                   ser setado via SetNamedSecurityInfo sem permissão de leitura)
    ///   retorno 2/3/53 → realmente não existe
    /// </summary>
    public static int ProbePath(string path, out bool exists, out bool isDir, out int errorCode)
    {
        exists = false;
        isDir = false;
        errorCode = 0;
        try
        {
            uint attrs = GetFileAttributesW(path);
            if (attrs != INVALID_FILE_ATTRIBUTES)
            {
                exists = true;
                isDir = (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
                return 0;
            }
            errorCode = Marshal.GetLastWin32Error();
            if (errorCode == ERROR_ACCESS_DENIED || errorCode == ERROR_NOT_READY)
            {
                // Existe, mas a ACL nega o probe — assumimos pasta; o SetNamedSecurityInfo
                // (WRITE_OWNER) funciona sem ler nada.
                exists = true;
                isDir = true;
                return errorCode;
            }
            return errorCode;
        }
        catch { return 0; }
    }

    // ───────── API pública ─────────

    public sealed class Result
    {
        public int Total;
        public int Success;
        public int Failed;
        public List<string> Errors = new();
        /// <summary>True quando o fallback clássico (takeown.exe + icacls) foi usado e completou.</summary>
        public bool FallbackUsed;
        /// <summary>Mensagem descritiva do fallback clássico.</summary>
        public string FallbackMessage = "";
        public bool Ok => Failed == 0;
    }

    /// <summary>
    /// Assume propriedade + FullControl de um arquivo ou pasta (recursivo se pasta).
    /// Tudo in-process: zero spawns, progress reportável, cancelável.
    /// Se restarem falhas (árvores totalmente negadas tipo Windows.old), roda o fallback
    /// clássico takeown.exe + icacls automaticamente.
    /// </summary>
    public static Result TakeOwn(string path, bool recursive, Action<int>? progress = null, CancellationToken ct = default)
        => TakeOwn(path, recursive, (done, total, file) => progress?.Invoke(done), ct);

    public static Result TakeOwn(string path, bool recursive, Action<int, int, string>? progressDetailed, CancellationToken ct = default)
        => TakeOwn(path, recursive, progressDetailed, grantFullControlOnDirs: true, ct);

    public static Result TakeOwn(string path, bool recursive, Action<int, int, string>? progressDetailed, bool grantFullControlOnDirs, CancellationToken ct = default)
        => TakeOwnCore(path, recursive, progressDetailed, grantFullControlOnDirs, allowClassicFallback: true, ct);

    public static Result TakeOwn(string path, bool recursive, Action<int, int, string>? progressDetailed, bool grantFullControlOnDirs, bool allowClassicFallback, CancellationToken ct = default)
        => TakeOwnCore(path, recursive, progressDetailed, grantFullControlOnDirs, allowClassicFallback, ct);

    private static Result TakeOwnCore(string path, bool recursive, Action<int, int, string>? progressDetailed, bool grantFullControlOnDirs, bool allowClassicFallback, CancellationToken ct)
    {
        var result = new Result();

        if (!EnablePrivileges())
        {
            string msg = "Privilégios de administrador indisponíveis (SeTakeOwnershipPrivilege não pôde ser habilitado). Execute o KitLugia como Administrador (UAC).";
            Logger.Log($"[TAKE OWNERSHIP] {msg}");
            result.Errors.Add(msg);
            result.Failed++;
            return result;
        }

        bool isDir;
        if (File.Exists(path)) isDir = false;
        else if (Directory.Exists(path)) isDir = true;
        else
        {
            int probe = ProbePath(path, out bool exists, out isDir, out int errCode);
            if (!exists && probe != ERROR_ACCESS_DENIED && probe != ERROR_NOT_READY)
            {
                result.Errors.Add($"Caminho não encontrado: {path} (erro {probe})");
                result.Failed++;
                Logger.Log($"[TAKE OWNERSHIP] Caminho não encontrado: {path} (erro {probe})");
                return result;
            }
            Logger.Log($"[TAKE OWNERSHIP] Caminho existe mas ACL nega leitura (erro {errCode}) — assumindo como pasta: {path}");
        }

        // FIX PT-BR: usar SID bem conhecido em vez de "Administrators"/"Administradores" hard-coded.
        // Em PT-BR o grupo é "Administradores", em EN é "Administrators" — NTAccount traduz falha.
        // SID S-1-5-32-544 funciona em qualquer idioma.
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        // OTIMIZADO: ambos sem herança (None) — cada item ganha ACE explícito via loop.
        // CI|OI faz o Windows propagar o ACE para TODOS os filhos em cada SetAccessControl,
        // causando O(n²): raiz 1739ms, KitLugia.GUI 963ms mesmo com folhas→raiz.
        // Com None cada SetAccessControl é O(1) (~2-5ms). Como já iteramos todos os itens
        // explicitamente, herança é redundante.
        var dirRule = new FileSystemAccessRule(
            adminSid, FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow);
        var fileRule = new FileSystemAccessRule(
            adminSid, FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow);

        // Pré-contagem (best effort) para o progresso — falha silenciosamente em árvores negadas.
        int preCount = 0;
        if (isDir && recursive)
        {
            try
            {
                var enumOpts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint, // NÃO seguir junctions/symlinks (loops!)
                    ReturnSpecialDirectories = false
                };
                preCount = Directory.EnumerateFileSystemEntries(path, "*", enumOpts).Count();
            }
            catch { }
        }
        result.Total = Math.Max(1, preCount + 1);

        int done = 0;
        void Report(string cur)
        {
            done++;
            if (done > result.Total) result.Total = done; // árvore negada descobriu mais itens
            if (done % 10 == 0 || done <= 3 || done == result.Total)
                progressDetailed?.Invoke(done, result.Total, cur);
        }

        // ── dono de um item ──
        void OwnOne(string target, bool dirTarget)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                if (!grantFullControlOnDirs)
                {
                    // MODO RÁPIDO: OWNER-only via SetNamedSecurityInfo — 70ms→1ms, 1838ms→20ms.
                    // Funciona SEM permissão de leitura (WRITE_OWNER + SeTakeOwnershipPrivilege),
                    // ou seja, também em arquivos/pastas 100% negados (Windows.old).
                    byte[] ownerBytes = new byte[adminSid.BinaryLength];
                    adminSid.GetBinaryForm(ownerBytes, 0);
                    int err = SetNamedSecurityInfo(target, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, ownerBytes, null, null, null);
                    if (err != 0) throw new Win32Exception(err);

                    // PROVA-DE-TUDO (03/09, testado em árvore com DENY explícito): só trocar o
                    // dono NÃO destrava itens com ACE de deny no DACL (ex: Lugia:(OI)(CI)(DENY)(RX))
                    // — deny vence até para o novo dono elevado. GetFileAttributesW NÃO detecta
                    // (passa em arquivo negado); o probe real é abrir para leitura (CreateFile
                    // GENERIC_READ). Se continuar inacessível após virar nosso, substitui o DACL
                    // por Administradores:F no MESMO SetNamedSecurityInfo (continua O(1), sem ler nada).
                    if (!CanReadOpen(target))
                    {
                        SetOwnerAndDaclNative(target, adminSid);
                        Logger.Log($"[TAKE OWNERSHIP] DENY detectado no DACL — DACL substituído por Administradores:F: {target}");
                    }
                }
                else if (dirTarget)
                {
                    // MODO COMPLETO (checkbox marcado): Owner + FullControl — lento mas garante ACE visível na pasta
                    var dInfo = new DirectoryInfo(target);
                    var dSec = dInfo.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
                    dSec.SetOwner(adminSid);
                    dSec.ResetAccessRule(dirRule);
                    dInfo.SetAccessControl(dSec);
                }
                else
                {
                    var fInfo = new FileInfo(target);
                    var fSec = fInfo.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
                    fSec.SetOwner(adminSid);
                    fSec.ResetAccessRule(fileRule);
                    fInfo.SetAccessControl(fSec);
                }
                result.Success++;
            }
            catch (UnauthorizedAccessException)
            {
                // ACL negou a LEITURA — substitui dono + DACL via Win32 (não precisa ler o atual)
                OwnDenied(target, dirTarget);
            }
            catch (Exception ex)
            {
                result.Failed++;
                if (result.Errors.Count < 20) result.Errors.Add($"{Path.GetFileName(target)}: {ex.Message}");
            }
        }

        void OwnDenied(string target, bool dirTarget)
        {
            try
            {
                SetOwnerAndDaclNative(target, adminSid);
                result.Success++;
                Logger.Log($"[TAKE OWNERSHIP] ACL substituída via Win32 (item negado): {target}");
            }
            catch (Exception ex)
            {
                result.Failed++;
                if (result.Errors.Count < 20) result.Errors.Add($"{Path.GetFileName(target)}: {ex.Message}");
            }
        }

        // ── execução ──
        if (!isDir)
        {
            OwnOne(path, dirTarget: false);
            Report(path);
        }
        else
        {
            // PAI PRIMEIRO (BFS): em árvores negadas (Windows.old), o dono da pasta destrava
            // o acesso do nível seguinte — folhas→raiz (ordem antiga) falhava em tudo.
            OwnOne(path, dirTarget: true);
            Report(path);

            if (recursive)
            {
                var queue = new Queue<string>();
                queue.Enqueue(path);
                while (queue.Count > 0)
                {
                    if (ct.IsCancellationRequested) break;
                    string dir = queue.Dequeue();

                    string[] files = Array.Empty<string>();
                    try { files = Directory.GetFiles(dir); } catch { /* negado mesmo após dono — tenta de novo abaixo */ }
                    if (files.Length == 0 && !ct.IsCancellationRequested)
                    {
                        // 2ª tentativa: após tomar posse da pasta, a leitura costuma destravar
                        try { files = Directory.GetFiles(dir); } catch { }
                    }
                    foreach (var f in files)
                    {
                        OwnOne(f, dirTarget: false);
                        Report(f);
                    }

                    string[] subs = Array.Empty<string>();
                    try { subs = Directory.GetDirectories(dir); } catch { }
                    if (subs.Length == 0 && !ct.IsCancellationRequested)
                    {
                        try { subs = Directory.GetDirectories(dir); } catch { }
                    }
                    foreach (var sub in subs)
                    {
                        // evita loop em junction/symlink (ex: Windows.old\Documents and Settings → C:\Users)
                        try
                        {
                            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch { continue; }
                        OwnOne(sub, dirTarget: true);
                        Report(sub);
                        queue.Enqueue(sub);
                    }
                }
            }
        }

        // ── Fallback clássico (a prova de tudo): takeown.exe + icacls com SID ──
        // takeown.exe usa semântica de backup internamente e é o método comprovado da
        // Microsoft para Windows.old (`takeown /f C:\Windows.old /r /d y`). Só roda se
        // sobraram falhas no pass in-process.
        if (allowClassicFallback && result.Failed > 0 && !ct.IsCancellationRequested)
        {
            var fb = RunClassicTakeOwn(path);
            result.FallbackUsed = true;
            if (fb.Ok)
            {
                result.FallbackMessage = fb.Message;
                result.Success = result.Total;
                result.Failed = 0;
                result.Errors.Clear();
                Logger.Log($"[TAKE OWNERSHIP] Fallback clássico completou: {fb.Message}");
            }
            else
            {
                result.FallbackMessage = "Fallback clássico também falhou: " + fb.Message;
                result.Errors.Add(result.FallbackMessage);
                Logger.Log($"[TAKE OWNERSHIP] {result.FallbackMessage}");
            }
        }

        Logger.Log($"[TAKE OWNERSHIP] {path}: {result.Success}/{result.Total} ok, {result.Failed} falhas{(ct.IsCancellationRequested ? " (cancelado)" : "")}{(result.FallbackUsed ? " [fallback clássico usado]" : "")}");
        return result;
    }

    /// <summary>Variante com profiling por arquivo — só para bench/diagnóstico.</summary>
    public static Result TakeOwnWithProfiling(string path, bool recursive, out Dictionary<string, long> perFileMs, CancellationToken ct = default)
    {
        perFileMs = new Dictionary<string, long>();
        var swTotal = Stopwatch.StartNew();
        var result = TakeOwn(path, recursive, (done, total, cur) => { }, ct);
        swTotal.Stop();
        return result;
    }

    /// <summary>Substitui dono + DACL (Administradores:F) via SetNamedSecurityInfo — não precisa de leitura.</summary>
    private static void SetOwnerAndDaclNative(string target, SecurityIdentifier ownerSid)
    {
        byte[] ownerBytes = new byte[ownerSid.BinaryLength];
        ownerSid.GetBinaryForm(ownerBytes, 0);

        var ace = new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl, ownerSid, false, null);
        var dacl = new RawAcl(GenericAcl.AclRevisionDS, 1);
        dacl.InsertAce(0, ace);

        // ⚠️ pDacl de SetNamedSecurityInfo é um PACL (ponteiro para ACL crua), NÃO um
        // SECURITY_DESCRIPTOR. Bug real (03/09, testado): passar os bytes de um descriptor
        // faz o Windows ler o cabeçalho do SD como cabeçalho de ACL — AceCount vira o
        // OwnerOffset (0) → DACL VAZIO gravado → nega TUDO (até para o dono elevado).
        byte[] aclBytes = new byte[dacl.BinaryLength];
        dacl.GetBinaryForm(aclBytes, 0);

        int err = SetNamedSecurityInfo(target, SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            ownerBytes, null, aclBytes, null);
        if (err != 0) throw new Win32Exception(err);
    }

    private static (bool Ok, string Message) RunClassicTakeOwn(string path)
    {
        try
        {
            Logger.Log($"[TAKE OWNERSHIP] Fallback clássico (takeown.exe + icacls, SID locale-proof) em: {path}");
            int code1 = RunTool("takeown.exe", $"/f \"{path}\" /r /d y", 15 * 60 * 1000);
            if (code1 != 0) return (false, $"takeown.exe exit {code1}");
            // *S-1-5-32-544 = Administradores (qualquer idioma). /c continua em erro, /l não segue symlinks.
            int code2 = RunTool("icacls.exe", $"\"{path}\" /grant *S-1-5-32-544:F /t /c /l /q", 15 * 60 * 1000);
            if (code2 != 0) return (false, $"icacls.exe exit {code2}");
            return (true, "Take ownership completo via takeown.exe + icacls (fallback clássico).");
        }
        catch (Exception ex)
        {
            Logger.Log($"[TAKE OWNERSHIP] Fallback clássico ERRO: {ex.Message}");
            return (false, ex.Message);
        }
    }

    private static int RunTool(string exe, string args, int timeoutMs)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (p == null) return -1;
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch { }
            return -2;
        }
        Logger.Log($"[TAKE OWNERSHIP] {exe} exit={p.ExitCode} out={(o.Length > 300 ? o[..300] : o)} err={(e.Length > 300 ? e[..300] : e)}");
        return p.ExitCode;
    }

    /// <summary>Restaura owner para TrustedInstaller (desfazer) — SID fixo, locale-proof.</summary>
    public static bool RestoreToTrustedInstaller(string path)
    {
        try
        {
            EnablePrivileges();
            // SID do TrustedInstaller (S-1-5-80-3139157870-2983391045-3678747466-658725712-1809340420)
            var ti = new SecurityIdentifier("S-1-5-80-3139157870-2983391045-3678747466-658725712-1809340420");

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                int probe = ProbePath(path, out _, out _, out _);
                if (probe != ERROR_ACCESS_DENIED && probe != ERROR_NOT_READY)
                    return false;
            }

            byte[] ownerBytes = new byte[ti.BinaryLength];
            ti.GetBinaryForm(ownerBytes, 0);
            int err = SetNamedSecurityInfo(path, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, ownerBytes, null, null, null);
            if (err != 0)
            {
                Logger.Log($"[TAKE OWNERSHIP] Restore TI falhou: {new Win32Exception(err).Message}");
                return false;
            }
            return true;
        }
        catch (Exception ex) { Logger.Log($"[TAKE OWNERSHIP] Restore TI falhou: {ex.Message}"); return false; }
    }
}