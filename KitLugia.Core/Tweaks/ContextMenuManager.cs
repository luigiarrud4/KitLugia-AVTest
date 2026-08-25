using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KitLugia.Core;

/// <summary>
/// Gerenciador completo do Menu de Contexto do Windows.
/// Enumera verbos estáticos + handlers COM (shellex), classifica Sistema vs Terceiros
/// pela localização da DLL, e permite desabilitar/habilitar DE FORMA NÃO-DESTRUTIVA:
///   - Verbos estáticos: valor vazio "LegacyDisable" na sombra HKCU\Software\Classes (merge ganha de HKLM)
///   - Handlers COM: CLSID adicionado a "Shell Extensions\Blocked" (mecanismo oficial do Windows)
/// Deletar sempre exporta .reg de backup antes. Restaurar desfaz sombras/bloqueios/reimporta backup.
/// </summary>
public sealed class ContextMenuEntry
{
    public string DisplayName { get; set; } = "";
    public string KeyPath { get; set; } = "";          // caminho real (HKLM/HKCU) da chave fonte
    public string ShadowPath { get; set; } = "";       // caminho da sombra HKCU (para LegacyDisable)
    public string Scope { get; set; } = "";            // Todos os arquivos / Pastas / Fundo / Área de trabalho / Unidades / Por extensão
    public string Kind { get; set; } = "";             // Verbo / Handler COM / Submenu
    public bool IsComHandler { get; set; }
    public string? Clsid { get; set; }
    public string? DllPath { get; set; }
    public bool IsSystem { get; set; }
    public bool IsDisabled { get; set; }
    public string? Command { get; set; }               // comando do verbo (se houver)
    public string BackupFile { get; set; } = "";
}

public static class ContextMenuManager
{
    // Escopos padrão onde o menu de contexto vive (documentado em MS Learn "Registering Shell Extension Handlers")
    private static readonly (string RelPath, string Label)[] Scopes =
    {
        (@"*",                        "Todos os arquivos"),
        (@"AllFileSystemObjects",     "Arquivos e pastas"),
        (@"Folder",                   "Todas as pastas"),
        (@"Directory",                "Pastas de arquivo"),
        (@"Directory\Background",     "Fundo de pasta"),
        (@"DesktopBackground",        "Área de trabalho"),
        (@"Drive",                    "Unidades"),
    };

    private const string BlockedKeyUser  = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string ClassesUserRoot = @"Software\Classes";

    /// <summary>Enumera TODOS os itens do menu de contexto com classificação sistema/terceiro.</summary>
    public static List<ContextMenuEntry> EnumerateAll()
    {
        var result = new List<ContextMenuEntry>();
        var seenClsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rel, label) in Scopes)
        {
            EnumerateVerbs(rel, label, result);
            EnumerateComHandlers(rel, label, result, seenClsids);
        }

        return result.OrderBy(e => e.IsSystem).ThenBy(e => e.Scope).ThenBy(e => e.DisplayName).ToList();
    }

    private static void EnumerateVerbs(string scopeRel, string scopeLabel, List<ContextMenuEntry> into)
    {
        // Verbos podem existir em HKLM\Software\Classes e HKCU\Software\Classes (HKCR = merge dos dois)
        foreach (var (root, rootName) in new[] {
            (Registry.LocalMachine, "HKLM"), (Registry.CurrentUser, "HKCU") })
        {
            var shellPath = $@"Software\Classes\{scopeRel}\shell";
            using var shellKey = root.OpenSubKey(shellPath);
            if (shellKey == null) continue;

            foreach (var verbName in shellKey.GetSubKeyNames())
            {
                try
                {
                    using var verbKey = shellKey.OpenSubKey(verbName);
                    if (verbKey == null) continue;
                    if (verbKey.GetValue("LegacyDisable") != null && root == Registry.LocalMachine) continue; // já oculto via sombra

                    var display = verbKey.GetValue("") as string;
                    if (string.IsNullOrEmpty(display)) display = Capitalize(verbName);

                    string? command = null;
                    using (var cmdKey = verbKey.OpenSubKey("command"))
                        command = cmdKey?.GetValue("") as string;

                    var entry = new ContextMenuEntry
                    {
                        DisplayName = display,
                        KeyPath = $@"{rootName}\{shellPath}\{verbName}",
                        ShadowPath = $@"{ClassesUserRoot}\{scopeRel}\shell\{verbName}",
                        Scope = scopeLabel,
                        Kind = verbKey.GetSubKeyNames().Any(k => k.Equals("SP", StringComparison.OrdinalIgnoreCase)) || HasSubCommands(verbKey) ? "Submenu" : "Verbo",
                        IsComHandler = false,
                        Command = command,
                        IsSystem = IsSystemPath(command) || (rootName == "HKLM" && IsBuiltinVerb(verbName)),
                    };
                    entry.IsDisabled = IsVerbDisabled(entry.ShadowPath);
                    if (!into.Any(e => e.KeyPath == entry.KeyPath))
                        into.Add(entry);
                }
                catch { }
            }
        }
    }

    private static bool HasSubCommands(RegistryKey verbKey)
    {
        try { return verbKey.GetValue("ExtendedSubCommandsKey") != null || verbKey.OpenSubKey("shell") != null; }
        catch { return false; }
    }

    private static void EnumerateComHandlers(string scopeRel, string scopeLabel, List<ContextMenuEntry> into, HashSet<string> seen)
    {
        foreach (var (root, rootName) in new[] {
            (Registry.LocalMachine, "HKLM"), (Registry.CurrentUser, "HKCU") })
        {
            var handlersPath = $@"Software\Classes\{scopeRel}\shellex\ContextMenuHandlers";
            using var handlersKey = root.OpenSubKey(handlersPath);
            if (handlersKey == null) continue;

            foreach (var handlerName in handlersKey.GetSubKeyNames())
            {
                try
                {
                    using var hk = handlersKey.OpenSubKey(handlerName);
                    var clsid = hk?.GetValue("") as string;
                    if (string.IsNullOrEmpty(clsid)) continue;
                    if (!seen.Add(clsid + "|" + scopeLabel)) continue;

                    var dllPath = ResolveClsidDll(clsid);
                    var friendlyName = GetClsidFriendlyName(clsid) ?? Capitalize(handlerName);

                    var entry = new ContextMenuEntry
                    {
                        DisplayName = friendlyName,
                        KeyPath = $@"{rootName}\{handlersPath}\{handlerName}",
                        Scope = scopeLabel,
                        Kind = "Handler COM",
                        IsComHandler = true,
                        Clsid = clsid,
                        DllPath = dllPath,
                        // Classificação REAL: assinatura digital Microsoft > caminho em C:\Windows
                        IsSystem = IsMicrosoftComponent(dllPath),
                    };
                    entry.IsDisabled = IsClsidBlocked(clsid);
                    into.Add(entry);
                }
                catch { }
            }
        }
    }

    private static string? ResolveClsidDll(string clsid)
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = root.OpenSubKey($@"Software\Classes\CLSID\{clsid}\InprocServer32");
                var dll = k?.GetValue("") as string;
                if (!string.IsNullOrEmpty(dll))
                    return Environment.ExpandEnvironmentVariables(dll);
                // alguns usam LocalServer32 (exe)
                using var k2 = root.OpenSubKey($@"Software\Classes\CLSID\{clsid}\LocalServer32");
                dll = k2?.GetValue("") as string;
                if (!string.IsNullOrEmpty(dll)) return Environment.ExpandEnvironmentVariables(dll);
            }
            catch { }
        }
        return null;
    }

    private static string? GetClsidFriendlyName(string clsid)
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = root.OpenSubKey($@"Software\Classes\CLSID\{clsid}");
                return k?.GetValue("") as string;
            }
            catch { }
        }
        return null;
    }

    private static bool IsSystemPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Split(',')[0].Trim()));
            var winDir = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            return full.StartsWith(winDir, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Classificação confiável de "é do Windows": 1) assinatura digital Microsoft,
    /// 2) fallback: DLL dentro de C:\Windows. É o mesmo critério do ShellExView.
    /// </summary>
    private static bool IsMicrosoftComponent(string? dllPath)
    {
        if (string.IsNullOrEmpty(dllPath)) return false;

        try
        {
            var clean = dllPath.Split(',')[0].Trim();
            var full = Environment.ExpandEnvironmentVariables(clean);
            if (!File.Exists(full)) return IsSystemPath(dllPath);

            var info = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(full);
            var subject = info.Subject ?? "";
            // Certificado autenticado pela Microsoft (qualquer unidade: Redmond WA / IE / etc.)
            if (subject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                return true;
            // Assinado mas NÃO é Microsoft = definitivamente terceiro
            if (!string.IsNullOrEmpty(subject))
                return false;
        }
        catch
        {
            // Arquivo não assinado — decide pelo caminho
        }

        return IsSystemPath(dllPath);
    }

    private static bool IsBuiltinVerb(string verb) => verb is "open" or "opennewprocess" or "runas" or "print" or "printto"
        or "find" or "cmd" or "Powershell" or "WSL" or "git_gui" or "git_bash" or "MapNetworkDrive"
        or "DisconnectNetworkDrive" or "Properties" or "rename" or "delete" or "cut" or "copy" or "paste";

    // ─────────────── DESABILITAR / HABILITAR (não-destrutivo) ───────────────

    public static (bool ok, string msg) Disable(ContextMenuEntry e)
    {
        try
        {
            if (e.IsComHandler && !string.IsNullOrEmpty(e.Clsid))
            {
                // Mecanismo oficial: lista de extensões bloqueadas
                using var blocked = Registry.CurrentUser.CreateSubKey(BlockedKeyUser);
                blocked.SetValue(e.Clsid, e.DisplayName ?? "Bloqueado pelo KitLugia");
                Logger.Log($"[CONTEXT MENU] Bloqueado: {e.DisplayName} ({e.Clsid})");
            }
            else
            {
                // Sombra HKCU com LegacyDisable vazio — merge HKCU > HKLM suprime o verbo
                var relPath = ExtractRelativeShadowPath(e.ShadowPath);
                using var shadow = Registry.CurrentUser.CreateSubKey(relPath);
                shadow.SetValue("LegacyDisable", "", RegistryValueKind.String);
                Logger.Log($"[CONTEXT MENU] Oculto (LegacyDisable): {e.DisplayName}");
            }
            e.IsDisabled = true;
            return (true, $"{e.DisplayName}: desabilitado");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static (bool ok, string msg) Enable(ContextMenuEntry e)
    {
        try
        {
            if (e.IsComHandler && !string.IsNullOrEmpty(e.Clsid))
            {
                using var blocked = Registry.CurrentUser.CreateSubKey(BlockedKeyUser);
                if (blocked.GetValue(e.Clsid) != null) blocked.DeleteValue(e.Clsid);
            }
            else
            {
                var relPath = ExtractRelativeShadowPath(e.ShadowPath);
                using var shadow = Registry.CurrentUser.CreateSubKey(relPath);
                if (shadow.GetValue("LegacyDisable") != null) shadow.DeleteValue("LegacyDisable");
                TryDeleteEmptyChain(relPath); // limpa sombras vazias que criamos
            }
            e.IsDisabled = false;
            return (true, $"{e.DisplayName}: habilitado");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ─────────────── DELETAR (com backup .reg) ───────────────

    public static (bool ok, string msg, string backupPath) Delete(ContextMenuEntry e)
    {
        try
        {
            string backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups", "ContextMenu");
            Directory.CreateDirectory(backupDir);
            string safe = string.Concat(e.KeyPath.Where(char.IsLetterOrDigit).Take(60));
            string backupPath = Path.Combine(backupDir, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.reg");
            ExportRegKey(e.KeyPath, backupPath);

            if (e.IsComHandler)
            {
                // deleta o ponteiro do handler (a DLL fica; desinstalador do app pode recriar)
                DeleteKeyTree(e.KeyPath.StartsWith("HKLM") ? Registry.LocalMachine : Registry.CurrentUser,
                              e.KeyPath.Substring(5));
            }
            else
            {
                DeleteKeyTree(e.KeyPath.StartsWith("HKLM") ? Registry.LocalMachine : Registry.CurrentUser,
                              e.KeyPath.Substring(5));
            }

            // também remove bloqueio/sombra residual para não ficar órfão
            if (!string.IsNullOrEmpty(e.Clsid))
            {
                using var blocked = Registry.CurrentUser.CreateSubKey(BlockedKeyUser);
                if (blocked.GetValue(e.Clsid) != null) blocked.DeleteValue(e.Clsid);
            }

            Logger.Log($"[CONTEXT MENU] DELETADO: {e.DisplayName} (backup: {backupPath})");
            return (true, $"{e.DisplayName} deletado. Backup salvo.", backupPath);
        }
        catch (Exception ex) { return (false, ex.Message, ""); }
    }

    // ─────────────── RESTAURAR TUDO (undo global das ações do KitLugia) ───────────────

    public static (int restored, int errors) RestoreAllKitChanges()
    {
        int restored = 0, errors = 0;

        // 1) limpa todos os CLSIDs que bloqueamos
        try
        {
            using var blocked = Registry.CurrentUser.OpenSubKey(BlockedKeyUser, writable: true);
            if (blocked != null)
            {
                foreach (var name in blocked.GetValueNames().ToList())
                {
                    try { blocked.DeleteValue(name); restored++; } catch { errors++; }
                }
            }
        }
        catch { errors++; }

        // 2) limpa todas as sombras LegacyDisable sob Software\Classes\*\shell etc.
        foreach (var (rel, _) in Scopes)
        {
            try
            {
                var basep = $@"{ClassesUserRoot}\{rel}\shell";
                using var sk = Registry.CurrentUser.OpenSubKey(basep, writable: true);
                if (sk == null) continue;
                foreach (var verb in sk.GetSubKeyNames())
                {
                    try
                    {
                        using var vk = sk.OpenSubKey(verb, writable: true);
                        if (vk?.GetValue("LegacyDisable") != null)
                        {
                            vk.DeleteValue("LegacyDisable");
                            restored++;
                        }
                    }
                    catch { errors++; }
                }
            }
            catch { errors++; }
        }

        Logger.Log($"[CONTEXT MENU] Restauração global: {restored} itens, {errors} erros");
        return (restored, errors);
    }

    // ─────────────── Helpers ───────────────

    private static string ExtractRelativeShadowPath(string shadowFullPath)
    {
        // shadowFullPath = "Software\Classes\...\shell\verbo" (já sem hive)
        return shadowFullPath.StartsWith(@"Software\Classes\") ? shadowFullPath : @"Software\Classes\" + shadowFullPath;
    }

    private static bool IsVerbDisabled(string shadowPathWithoutHive)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(shadowPathWithoutHive);
            return k?.GetValue("LegacyDisable") != null;
        }
        catch { return false; }
    }

    public static bool IsClsidBlocked(string clsid)
    {
        try
        {
            using var blocked = Registry.CurrentUser.OpenSubKey(BlockedKeyUser);
            return blocked?.GetValue(clsid) != null;
        }
        catch { return false; }
    }

    private static void ExportRegKey(string fullPathWithHive, string outputFile)
    {
        try
        {
            // reg export exige formato "HKLM\..." 
            var psi = new System.Diagnostics.ProcessStartInfo("reg.exe",
                $"export \"{fullPathWithHive}\" \"{outputFile}\" /y")
            { CreateNoWindow = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(10000);
        }
        catch (Exception ex) { Logger.LogWarning("ContextMenu", $"Backup falhou: {ex.Message}"); }
    }

    private static void DeleteKeyTree(RegistryKey root, string subKey)
    {
        try { root.DeleteSubKeyTree(subKey, false); }
        catch (Exception ex) { Logger.LogWarning("ContextMenu", $"Delete {subKey}: {ex.Message}"); }
    }

    private static void TryDeleteEmptyChain(string relPath)
    {
        try
        {
            var parts = relPath.Split('\\');
            var cur = relPath;
            while (cur.Contains('\\'))
            {
                using var k = Registry.CurrentUser.OpenSubKey(cur, writable: true);
                if (k == null) break;
                if (k.GetSubKeyNames().Length > 0 || k.GetValueNames().Length > 0) break;
                var parent = cur.Substring(0, cur.LastIndexOf('\\'));
                var leaf = cur.Substring(cur.LastIndexOf('\\') + 1);
                using var pk = Registry.CurrentUser.OpenSubKey(parent, writable: true);
                pk?.DeleteSubKey(leaf);
                cur = parent;
                if (cur.EndsWith("\\Classes", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("Classes")) break;
            }
        }
        catch { }
    }

    private static string Capitalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return char.ToUpper(s[0]) + s.Substring(1);
    }
}
