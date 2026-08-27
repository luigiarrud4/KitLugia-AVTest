using System;
using System.Collections.Generic;
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

    private const int SE_FILE_OBJECT = 1;
    private const int OWNER_SECURITY_INFORMATION = 0x00000001;
    private const int DACL_SECURITY_INFORMATION = 0x00000004;

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const int SE_PRIVILEGE_ENABLED = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public long Luid;
        public int Attributes;
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

    // ───────── API pública ─────────

    public sealed class Result
    {
        public int Total;
        public int Success;
        public int Failed;
        public List<string> Errors = new();
        public bool Ok => Failed == 0;
    }

    /// <summary>
    /// Assume propriedade + FullControl de um arquivo ou pasta (recursivo se pasta).
    /// Tudo in-process: zero spawns, progress reportável, cancelável.
    /// </summary>
    public static Result TakeOwn(string path, bool recursive, Action<int>? progress = null, CancellationToken ct = default)
        => TakeOwn(path, recursive, (done, total, file) => progress?.Invoke(done), ct);

    public static Result TakeOwn(string path, bool recursive, Action<int, int, string>? progressDetailed, CancellationToken ct = default)
        => TakeOwn(path, recursive, progressDetailed, grantFullControlOnDirs: true, ct);

    public static Result TakeOwn(string path, bool recursive, Action<int, int, string>? progressDetailed, bool grantFullControlOnDirs, CancellationToken ct = default)
    {
        var result = new Result();
        EnablePrivileges();

        // FIX PT-BR: usar SID bem conhecido em vez de "Administrators"/"Administradores" hard-coded.
        // Em PT-BR o grupo é "Administradores", em EN é "Administrators" — NTAccount traduz falha.
        // SID S-1-5-32-544 funciona em qualquer idioma.
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        // OTIMIZADO: ambos sem herança (None) — cada item ganha ACE explícito via loop.
        // CI|OI faz o Windows propagar o ACE para TODOS os filhos em cada SetAccessControl,
        // causando O(n²): raiz 1739ms, KitLugia.GUI 963ms mesmo com folhas→raiz.
        // Com None cada SetAccessControl é O(1) (~2-5ms). Como já iteramos todos os 11k itens
        // explicitamente, herança é redundante. Novos arquivos herdarão do pai via ACE explícito do pai? Não,
        // mas para TakeOwnership (destravar pra deletar/editar) explícito é mais rápido e suficiente.
        var dirRule = new FileSystemAccessRule(
            adminSid, FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow);
        var fileRule = new FileSystemAccessRule(
            adminSid, FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow);

        // coleta alvos primeiro (para progresso real) — com EnumerationOptions para não abortar em AccessDenied
        var targets = new List<string>();
        if (File.Exists(path)) targets.Add(path);
        else if (Directory.Exists(path))
        {
            targets.Add(path);
            if (recursive)
            {
                var enumOpts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0, // não pular hidden/system — justamente os travados
                    ReturnSpecialDirectories = false
                };
                try
                {
                    // EnumerateFiles/EnumerateDirectories com IgnoreInaccessible não aborta no primeiro AccessDenied
                    foreach (var f in Directory.EnumerateFiles(path, "*", enumOpts))
                        targets.Add(f);
                    foreach (var d in Directory.EnumerateDirectories(path, "*", enumOpts))
                        targets.Add(d);
                }
                catch { /* fallback: itens inacessíveis serão tentados um a um abaixo */ }

                // Fallback manual para casos onde Enumerate com IgnoreInaccessible ainda falha (junctions, long path)
                if (targets.Count == 1)
                {
                    try { CollectRecursiveFallback(path, targets); } catch { }
                }
            }
        }
        else { result.Errors.Add($"Caminho não encontrado: {path}"); result.Failed++; return result; }

        // OTIMIZAÇÃO folhas→raiz: a raiz sozinha custava 1605ms porque gravar herança varre os 11k filhos.
        // Processando do mais profundo pro raso cada SetAccessControl pega subárvore já corrigida.
        targets = targets.OrderByDescending(p => p.Length).ThenByDescending(p => p.Count(c => c == '\\')).ToList();

        result.Total = targets.Count;
        int done = 0;

        foreach (var target in targets)
        {
            if (ct.IsCancellationRequested) break;

            var swGet = System.Diagnostics.Stopwatch.StartNew();
            var swSet = System.Diagnostics.Stopwatch.StartNew();
            long getMs = 0, setMs = 0;
            bool isDir = false;
            try
            {
                isDir = Directory.Exists(target);
                if (isDir)
                {
                    if (grantFullControlOnDirs)
                    {
                        // MODO COMPLETO (checkbox marcado): Owner + FullControl — lento mas garante ACE visível na pasta
                        var dInfo = new DirectoryInfo(target);
                        swGet.Restart();
                        var dSec = dInfo.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
                        swGet.Stop(); getMs = swGet.ElapsedMilliseconds;
                        dSec.SetOwner(adminSid);
                        dSec.ResetAccessRule(dirRule);
                        swSet.Restart();
                        dInfo.SetAccessControl(dSec);
                        swSet.Stop(); setMs = swSet.ElapsedMilliseconds;
                    }
                    else
                    {
                        // MODO RÁPIDO (checkbox desmarcado): OWNER-only via SetNamedSecurityInfo — 70ms→1ms, 1838ms→20ms
                        swGet.Restart();
                        byte[] ownerBytes = new byte[adminSid.BinaryLength];
                        adminSid.GetBinaryForm(ownerBytes, 0);
                        swGet.Stop(); getMs = swGet.ElapsedMilliseconds;
                        swSet.Restart();
                        int err = SetNamedSecurityInfo(target, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, ownerBytes, null, null, null);
                        swSet.Stop(); setMs = swSet.ElapsedMilliseconds;
                        if (err != 0) throw new System.ComponentModel.Win32Exception(err);
                    }
                }
                else
                {
                    var fInfo = new FileInfo(target);
                    swGet.Restart();
                    var fSec = fInfo.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
                    swGet.Stop(); getMs = swGet.ElapsedMilliseconds;
                    fSec.SetOwner(adminSid);
                    fSec.ResetAccessRule(fileRule);
                    swSet.Restart();
                    fInfo.SetAccessControl(fSec);
                    swSet.Stop(); setMs = swSet.ElapsedMilliseconds;
                }
                result.Success++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                if (result.Errors.Count < 20) result.Errors.Add($"{Path.GetFileName(target)}: {ex.Message}");
            }
            long totalMs = getMs + setMs;
            if (totalMs > 30)
                Logger.Log($"[TAKE OWNERSHIP SLOW] {totalMs}ms (get {getMs}ms + set {setMs}ms) {(isDir?"DIR":"FILE")} → {target}");

            done++;
            if (done % 10 == 0 || done == result.Total) progressDetailed?.Invoke(done, result.Total, target);
            else if (done <= 3) progressDetailed?.Invoke(done, result.Total, target); // primeiros 3 sempre mostra
        }

        Logger.Log($"[TAKE OWNERSHIP] {path}: {result.Success}/{result.Total} ok, {result.Failed} falhas{(ct.IsCancellationRequested ? " (cancelado)" : "")}");
        return result;
    }

    /// <summary>Variante com profiling por arquivo — só para bench/diagnóstico.</summary>
    public static Result TakeOwnWithProfiling(string path, bool recursive, out Dictionary<string,long> perFileMs, CancellationToken ct = default)
    {
        perFileMs = new Dictionary<string,long>();
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var result = TakeOwn(path, recursive, (done,total,cur)=>{}, ct);
        swTotal.Stop();
        return result;
    }

    private static void CollectRecursiveFallback(string root, List<string> outList)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var sub in subs)
            {
                outList.Add(sub);
                // evita loop em junction/symlink (ReparsePoint)
                try
                {
                    var attr = File.GetAttributes(sub);
                    if ((attr & FileAttributes.ReparsePoint) == 0)
                        stack.Push(sub);
                }
                catch { stack.Push(sub); }
            }
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { continue; }
            foreach (var f in files) outList.Add(f);
        }
    }

    /// <summary>Restaura owner para TrustedInstaller (desfazer).</summary>
    public static bool RestoreToTrustedInstaller(string path)
    {
        try
        {
            EnablePrivileges();
            var ti = new NTAccount("NT SERVICE", "TrustedInstaller");

            if (Directory.Exists(path))
            {
                var dInfo = new DirectoryInfo(path);
                var sec = dInfo.GetAccessControl(AccessControlSections.Owner);
                sec.SetOwner(ti);
                dInfo.SetAccessControl(sec);
                return true;
            }
            if (File.Exists(path))
            {
                var fInfo = new FileInfo(path);
                var sec = fInfo.GetAccessControl(AccessControlSections.Owner);
                sec.SetOwner(ti);
                fInfo.SetAccessControl(sec);
                return true;
            }
            return false;
        }
        catch (Exception ex) { Logger.Log($"[TAKE OWNERSHIP] Restore TI falhou: {ex.Message}"); return false; }
    }
}
