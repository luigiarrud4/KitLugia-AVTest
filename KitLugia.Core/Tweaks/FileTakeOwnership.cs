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
                    var tp = new TOKEN_PRIVILEGES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
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
    {
        var result = new Result();
        EnablePrivileges();

        var admins = new NTAccount("BUILTIN", "Administrators");
        var rule = new FileSystemAccessRule(
            admins, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow);

        // coleta alvos primeiro (para progresso real)
        var targets = new List<string>();
        if (File.Exists(path)) targets.Add(path);
        else if (Directory.Exists(path))
        {
            targets.Add(path);
            if (recursive)
            {
                try
                {
                    targets.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
                    targets.AddRange(Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories));
                }
                catch { /* itens inacessíveis são justamente os que vamos corrigir */ }
            }
        }
        else { result.Errors.Add($"Caminho não encontrado: {path}"); result.Failed++; return result; }

        result.Total = targets.Count;
        int done = 0;

        foreach (var target in targets)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                if (Directory.Exists(target))
                {
                    var dInfo = new DirectoryInfo(target);
                    var dSec = dInfo.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group);
                    dSec.SetOwner(admins);
                    dInfo.SetAccessControl(dSec);

                    var dSec2 = dInfo.GetAccessControl(AccessControlSections.Access);
                    dSec2.ResetAccessRule(rule); // substitui denies, adiciona allow full
                    dInfo.SetAccessControl(dSec2);
                }
                else
                {
                    var fInfo = new FileInfo(target);
                    var fSec = fInfo.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group);
                    fSec.SetOwner(admins);
                    fInfo.SetAccessControl(fSec);

                    var fSec2 = fInfo.GetAccessControl(AccessControlSections.Access);
                    fSec2.ResetAccessRule(rule);
                    fInfo.SetAccessControl(fSec2);
                }
                result.Success++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                if (result.Errors.Count < 20) result.Errors.Add($"{Path.GetFileName(target)}: {ex.Message}");
            }

            done++;
            if (done % 10 == 0 || done == result.Total) progress?.Invoke(done);
        }

        Logger.Log($"[TAKE OWNERSHIP] {path}: {result.Success}/{result.Total} ok, {result.Failed} falhas{(ct.IsCancellationRequested ? " (cancelado)" : "")}");
        return result;
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
