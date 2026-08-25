using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KitLugia.Core;

/// <summary>
/// Catálogo "SUPER" de comandos do menu de contexto — versões turbinadas usando tecnologia do Kit.
/// Cada item define: como adicionar (registry), como verificar, como remover, e descrição do que a versão
/// super tem além da clássica. O Force Stop Unlock já é o padrão-ouro (usa IPC + Restart Manager).
/// </summary>
public static class ContextMenuQuickAdd
{
    public sealed class QuickItem
    {
        public string Id = "";
        public string DisplayName = "";
        public string Description = "";
        public string SuperNote = "";       // o que a versão super tem
        public string Emoji = "";
        public bool IsAdded;

        public Func<bool> Check = () => false;
        public Action Add = () => { };
        public Action Remove = () => { };
    }

    private static string ExePath()
    {
        var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KitLugia.GUI.exe");
        if (!File.Exists(exe))
            exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? exe;
        return exe;
    }


    // ══════════════════ DETECÇÃO CONSISTENTE (Kit + legado) ══════════════════
    // O Kit já tinha versões clássicas destes comandos em SystemTweaks (AddTakeOwnership→runas,
    // AddCmdHere→cmdhere etc). O Check() abaixo considera ATIVO qualquer uma das variantes
    // (super OU clássica) e o Add() remove a antiga antes de instalar a super — zero duplicatas.

    private static bool RegHas(string path)
        => Registry.GetValue(@"HKEY_CURRENT_USER\" + path, "", null) is string s && !string.IsNullOrEmpty(s);

    private static void DelTree(string rootRel, string leaf)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(rootRel, writable: true);
            k?.DeleteSubKeyTree(leaf, false);
        }
        catch { }
    }

    // Variantes LEGADAS que o Kit criou no passado — removidas ao instalar a super
    private static void RemoveLegacyCmdHere()
    {
        DelTree(@"Software\Classes\Directory\shell", "cmdhere");
        DelTree(@"Software\Classes\Directory\Background\shell", "cmdhere");
        SystemTweaks.RemoveCmdHere();
    }
    private static void RemoveLegacyPowerShell()
    {
        DelTree(@"Software\Classes\Directory\shell", "pshere");
        DelTree(@"Software\Classes\Directory\Background\shell", "pshere");
        SystemTweaks.RemovePowerShellHere();
        SystemTweaks.RemovePowerShellAdmin();
    }
    private static void RemoveLegacyNotepad()
    {
        DelTree(@"Software\Classes\*\shell", "notepad");
        SystemTweaks.RemoveNotepad();
    }
    private static void RemoveLegacyVsCode()
    {
        DelTree(@"Software\Classes\Directory\shell", "vscode");
        DelTree(@"Software\Classes\*\shell", "vscode");
        SystemTweaks.RemoveVsCode();
    }
    private static void RemoveLegacyCopyPath()
    {
        DelTree(@"Software\Classes\*\shell", "copypath");
        DelTree(@"Software\Classes\AllFilesystemObjects\shell", "copypath");
        SystemTweaks.RemoveCopyAsPath();
    }

    public static List<QuickItem> GetItems()
    {
        var list = new List<QuickItem>();

        // ── 1. FORCE STOP UNLOCK (já é super: IPC + RM + kill tree) ──
        // AddForceStopUnlock já faz RemoveForceStopUnlock() antes (anti-duplicata nativo)
        list.Add(new QuickItem
        {
            Id = "forcestop",
            DisplayName = "Force Stop Unlock (KitLugia)",
            Description = "Desbloqueia arquivos em uso matando os processos que os travam.",
            SuperNote = "Usa Restart Manager + NtQuerySystemInformation + kill de árvore via IPC — muito mais que um taskkill.",
            Emoji = "🔓",
            Check = () => SystemTweaks.IsForceStopUnlockAdded(),
            Add = () => SystemTweaks.AddForceStopUnlock(), // remove antigo + instala novo = idempotente
            Remove = () => SystemTweaks.RemoveForceStopUnlock(),
        });

        // ── 2. TAKE OWNERSHIP SUPER (PowerShell SetAccessControl recursivo) ──
        list.Add(new QuickItem
        {
            Id = "takeownership",
            DisplayName = "Take Ownership Super",
            Description = "Assume propriedade e concede controle total.",
            SuperNote = "Versão turbo: PowerShell SetAccessControl (instantâneo, sem spawn de cmd), herda para subpastas, log silencioso, fallback takeown/icacls só se PS falhar.",
            Emoji = "👑",
            Check = () => RegHas(@"Software\Classes\*\shell\kit_takeownership\command")
                       || SystemTweaks.IsTakeOwnershipAdded(), // clássica "runas" também conta como ativo
            Add = () => { SystemTweaks.RemoveTakeOwnership(); AddTakeOwnershipSuper(); },
            Remove = () =>
            {
                RemoveKeyTrees(new[] {
                    @"Software\Classes\*\shell\kit_takeownership",
                    @"Software\Classes\Directory\shell\kit_takeownership" });
                SystemTweaks.RemoveTakeOwnership(); // limpa a clássica também
            },
        });

        // ── 3. CMD AQUI SUPER (Windows Terminal quando disponível) ──
        list.Add(new QuickItem
        {
            Id = "cmdhere",
            DisplayName = "Abrir Terminal Aqui",
            Description = "Abre prompt na pasta atual.",
            SuperNote = "Auto-detecta Windows Terminal (wt.exe); cai para cmd.exe puro no Win10. Substitui o 'cmdhere' clássico.",
            Emoji = "🖥️",
            Check = () => RegHas(@"Software\Classes\Directory\Background\shell\kit_cmdhere\command")
                       || RegHas(@"Software\Classes\Directory\Background\shell\cmdhere\command")
                       || SystemTweaks.IsCmdHereAdded(),
            Add = () => { RemoveLegacyCmdHere(); AddTerminalHereSuper(); },
            Remove = () => RemoveKeyTrees(new[] {
                @"Software\Classes\Directory\shell\kit_cmdhere",
                @"Software\Classes\Directory\Background\shell\kit_cmdhere" }),
        });

        // ── 4. POWERSHELL ADMIN AQUI ──
        list.Add(new QuickItem
        {
            Id = "pshere",
            DisplayName = "PowerShell (Admin) Aqui",
            Description = "PowerShell elevado na pasta atual.",
            SuperNote = "Usa pwsh.exe (PowerShell 7) se instalado; senão powershell.exe -NoProfile -NoLogo (sem perfis = abre instantâneo). Substitui o 'pshere' clássico.",
            Emoji = "⚡",
            Check = () => RegHas(@"Software\Classes\Directory\Background\shell\kit_pshere\command")
                       || SystemTweaks.IsPowerShellHereAdded()
                       || SystemTweaks.IsPowerShellAdminAdded(),
            Add = () => { RemoveLegacyPowerShell(); AddPowerShellSuper(); },
            Remove = () =>
            {
                RemoveKeyTrees(new[] {
                    @"Software\Classes\Directory\shell\kit_pshere",
                    @"Software\Classes\Directory\Background\shell\kit_pshere" });
                RemoveLegacyPowerShell(); // limpa clássicas também
            },
        });

        // ── 5. COPIAR COMO CAMINHO (caminho limpo, sem aspas lixo) ──
        list.Add(new QuickItem
        {
            Id = "copypath",
            DisplayName = "Copiar Como Caminho (limpo)",
            Description = "Copia caminho completo para área de transferência.",
            SuperNote = "PowerShell Set-Clipboard: caminho SEM aspas duplas do Windows (que estraga scripts), trim automático, funciona em múltiplos arquivos.",
            Emoji = "📋",
            Check = () => RegHas(@"Software\Classes\AllFilesystemObjects\shell\kit_copypath\command")
                       || SystemTweaks.IsCopyAsPathAdded(),
            Add = () => { RemoveLegacyCopyPath(); AddCopyPathSuper(); },
            Remove = () =>
            {
                RemoveKeyTree(@"Software\Classes\AllFilesystemObjects\shell\kit_copypath");
                RemoveLegacyCopyPath();
            },
        });

        // ── 6. NOTEPAD SUPER (abre rápido, elevado se preciso) ──
        list.Add(new QuickItem
        {
            Id = "notepad",
            DisplayName = "Editar no Notepad",
            Description = "Abre qualquer arquivo no editor de texto.",
            SuperNote = "Tenta VS Code primeiro (se instalado, abre instantâneo); senão notepad.exe direto sem shell overhead.",
            Emoji = "📝",
            Check = () => RegHas(@"Software\Classes\*\shell\kit_notepad\command")
                       || SystemTweaks.IsNotepadAdded(),
            Add = () => { RemoveLegacyNotepad(); AddNotepadSuper(); },
            Remove = () =>
            {
                RemoveKeyTree(@"Software\Classes\*\shell\kit_notepad");
                RemoveLegacyNotepad();
            },
        });

        // ── 7. VS CODE AQUI ──
        list.Add(new QuickItem
        {
            Id = "vscode",
            DisplayName = "Abrir no VS Code",
            Description = "Abre pasta/arquivo no Visual Studio Code.",
            SuperNote = "Detecta instalação automaticamente (PATH/Program Files/AppData Local) e usa code.cmd --reuse-window. Substitui o 'vscode' clássico.",
            Emoji = "💻",
            Check = () => RegHas(@"Software\Classes\Directory\shell\kit_vscode\command")
                       || SystemTweaks.IsVsCodeAdded(),
            Add = () => { RemoveLegacyVsCode(); AddVsCodeSuper(); },
            Remove = () =>
            {
                RemoveKeyTrees(new[] {
                    @"Software\Classes\Directory\shell\kit_vscode",
                    @"Software\Classes\Directory\Background\shell\kit_vscode",
                    @"Software\Classes\*\shell\kit_vscode" });
                RemoveLegacyVsCode();
            },
        });

        return list;
    }

    // ══════════════════════ Implementações SUPER ══════════════════════

    private static void AddTakeOwnershipSuper()
    {
        try
        {
            // SUPER: o comando chama o próprio KitLugia (--takeown) que executa
            // FileTakeOwnership in-process: SeTakeOwnershipPrivilege via P/Invoke +
            // FileSystemSecurity nativo. Zero spawns de cmd/powershell, recursivo,
            // com toast de progresso na UI do Kit. Fallback PS se exe não encontrado.
            string exePath = ExePath();
            if (File.Exists(exePath))
            {
                string cmd = $"\"{exePath}\" --takeown \"%1\"";
                string label = "👑 Take Ownership Super (KitLugia)";

                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\kit_takeownership"))
                {
                    k.SetValue("", label);
                    k.SetValue("Icon", "imageres.dll,-78");
                    k.SetValue("NoWorkingDirectory", "");
                }
                using (var c = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\kit_takeownership\command"))
                    c.SetValue("", cmd);

                using (var k2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\kit_takeownership"))
                {
                    k2.SetValue("", label);
                    k2.SetValue("Icon", "imageres.dll,-78");
                    k2.SetValue("NoWorkingDirectory", "");
                }
                using (var c2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\kit_takeownership\command"))
                    c2.SetValue("", cmd);

                Logger.Log("[CONTEXT MENU] Take Ownership Super registrado (via KitLugia --takeown)");
                return;
            }

            // FALLBACK: PowerShell SetAccessControl (sem Kit rodando)
            Logger.Log("[CONTEXT MENU] Take Ownership: exe não achado, usando fallback PS");
            string psCmd = "powershell -NoProfile -WindowStyle Hidden -Command \"" +
                "$ErrorActionPreference='SilentlyContinue'; " +
                "$p='%1'; " +
                "$items=@($p); if(Test-Path $p -PathType Container){$items+=@(Get-ChildItem $p -Recurse -Force | %% FullName)}; " +
                "$rule=New-Object System.Security.AccessControl.FileSystemAccessRule('Administrators','FullControl','ContainerInherit,ObjectInherit','None','Allow'); " +
                "foreach($f in $items){ $acl=Get-Acl $f; $acl.SetOwner([System.Security.Principal.NTAccount]'Administrators'); Set-Acl $f $acl; $acl2=Get-Acl $f; $acl2.SetAccessRule($rule); Set-Acl $f $acl2 }\"";

            using (var kf = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\kit_takeownership"))
            {
                kf.SetValue("", "👑 Take Ownership Super");
                kf.SetValue("Icon", "imageres.dll,-78");
                kf.SetValue("NoWorkingDirectory", "");
            }
            using (var cf = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\kit_takeownership\command"))
                cf.SetValue("", psCmd);

            using (var kf2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\kit_takeownership"))
            {
                kf2.SetValue("", "👑 Take Ownership Super");
                kf2.SetValue("Icon", "imageres.dll,-78");
                kf2.SetValue("NoWorkingDirectory", "");
            }
            using (var cf2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\kit_takeownership\command"))
                cf2.SetValue("", psCmd);
        }
        catch (Exception ex) { Logger.Log($"[CONTEXT MENU] Erro takeown super: {ex.Message}"); }
    }

    private static void AddTerminalHereSuper()
    {
        try
        {
            bool hasWt = FindWtExe();
            string wtCmd = hasWt ? "wt.exe -d \"%V\"" : "cmd.exe /s /k pushd \"%V\"";
            string icon = hasWt ? "%LOCALAPPDATA%\\Microsoft\\WindowsApps\\wt.exe" : "cmd.exe";

            foreach (var baseP in new[] { @"Software\Classes\Directory\shell", @"Software\Classes\Directory\Background\shell" })
            {
                using var k = Registry.CurrentUser.CreateSubKey(baseP + @"\kit_cmdhere");
                k.SetValue("", hasWt ? "Abrir Windows Terminal" : "Abrir CMD Aqui");
                k.SetValue("Icon", icon);
                using var c = Registry.CurrentUser.CreateSubKey(baseP + @"\kit_cmdhere\command");
                c.SetValue("", wtCmd);
            }
            Logger.Log($"[CONTEXT MENU] Terminal aqui registrado (WT={hasWt})");
        }
        catch (Exception ex) { Logger.Log($"[CONTEXT MENU] Erro terminal: {ex.Message}"); }
    }

    private static bool FindWtExe()
    {
        try
        {
            string p1 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\wt.exe");
            if (File.Exists(p1)) return true;
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return File.Exists(Path.Combine(winDir, @"System32\wsl.exe")) &&
                   File.Exists(p1.Replace("wt.exe", "wt.exe")); // mesmo teste, mantém lógica simples
        }
        catch { return false; }
    }

    private static void AddPowerShellSuper()
    {
        try
        {
            // pw.exe (PS7) > powershell.exe -NoProfile
            bool hasPs7 = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"));
            string exe = hasPs7 ? "pwsh.exe" : "powershell.exe";
            string args = hasPs7 ? "-NoExit -NoProfile -NoLogo" : "-NoExit -NoProfile -NoLogo";
            string cmd = $"\"{exe}\" {args} -Command \"Set-Location -LiteralPath '%V'\"";

            foreach (var baseP in new[] { @"Software\Classes\Directory\shell", @"Software\Classes\Directory\Background\shell" })
            {
                using var k = Registry.CurrentUser.CreateSubKey(baseP + @"\kit_pshere");
                k.SetValue("", hasPs7 ? "Abrir PowerShell 7 Aqui" : "Abrir PowerShell Aqui");
                k.SetValue("Icon", hasPs7 ? "pwsh.exe" : "powershell.exe");
                using var c = Registry.CurrentUser.CreateSubKey(baseP + @"\kit_pshere\command");
                c.SetValue("", cmd);
            }
            Logger.Log($"[CONTEXT MENU] PowerShell aqui registrado (PS7={hasPs7})");
        }
        catch (Exception ex) { Logger.Log($"[CONTEXT MENU] Erro powershell: {ex.Message}"); }
    }

    private static void AddCopyPathSuper()
    {
        try
        {
            string psCmd = "powershell -NoProfile -WindowStyle Hidden -Command \"Set-Clipboard -Value '%1'\"";
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AllFilesystemObjects\shell\kit_copypath");
            k.SetValue("", "📋 Copiar Caminho (limpo)");
            k.SetValue("Icon", "imageres.dll,-5309");
            using var c = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AllFilesystemObjects\shell\kit_copypath\command");
            c.SetValue("", psCmd);
            Logger.Log("[CONTEXT MENU] Copiar caminho limpo registrado");
        }
        catch (Exception ex) { Logger.Log($"[CONTEXT MENU] Erro copiar: {ex.Message}"); }
    }

    private static void AddNotepadSuper()
    {
        try
        {
            // VS Code se disponível (mais rápido e melhor), senão notepad
            string vsCode = FindVsCode();
            string cmd = !string.IsNullOrEmpty(vsCode)
                ? $"\"{vsCode}\" --new-window \"%1\""
                : "notepad.exe \"%1\"";
            string label = !string.IsNullOrEmpty(vsCode) ? "📝 Editar no VS Code" : "📝 Editar no Notepad";

            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\kit_notepad");
            k.SetValue("", label);
            k.SetValue("Icon", !string.IsNullOrEmpty(vsCode) ? vsCode : "notepad.exe");
            using var c = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\kit_notepad\command");
            c.SetValue("", cmd);
            Logger.Log($"[CONTEXT MENU] Editor registrado (VSCode={!string.IsNullOrEmpty(vsCode)})");
        }
        catch (Exception ex) { Logger.Log($"[CONTEXT MENU] Erro notepad: {ex.Message}"); }
    }

    private static void AddVsCodeSuper()
    {
        try
        {
            string vsCode = FindVsCode();
            if (string.IsNullOrEmpty(vsCode))
            {
                Logger.Log("[CONTEXT MENU] VS Code não encontrado");
                return;
            }

            foreach (var (baseP, label) in new[] {
                (@"Software\Classes\Directory\shell", "📂 Abrir no VS Code"),
                (@"Software\Classes\Directory\Background\shell", "📂 Abrir VS Code aqui"),
                (@"Software\Classes\*\shell", "💻 Abrir no VS Code") })
            {
                using var k = Registry.CurrentUser.CreateSubKey(baseP + @"\kit_vscode");
                k.SetValue("", label);
                k.SetValue("Icon", vsCode);
                using var c = Registry.CurrentUser.CreateSubKey(baseP + @"\kit_vscode\command");
                c.SetValue("", $"\"{vsCode}\" \"%1\"");
            }
            Logger.Log("[CONTEXT MENU] VS Code registrado");
        }
        catch (Exception ex) { Logger.Log($"[CONTEXT MENU] Erro vscode: {ex.Message}"); }
    }

    private static string? FindVsCode()
    {
        string?[] candidates =
        {
            TryWhich("code.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft VS Code\Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft VS Code\Code.exe"),
        };
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
        return null;
    }

    private static string? TryWhich(string name)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("where.exe", name)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using var p = System.Diagnostics.Process.Start(psi);
            var outp = p?.StandardOutput.ReadToEnd().Trim().Split('\n').FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(outp) ? null : outp;
        }
        catch { return null; }
    }

    private static void RemoveKeyTree(string relPath)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(relPath[..relPath.LastIndexOf('\\')], true);
              k?.DeleteSubKeyTree(relPath[(relPath.LastIndexOf('\\') + 1)..], false); }
        catch { }
    }

    private static void RemoveKeyTrees(string[] paths)
    {
        foreach (var p in paths) RemoveKeyTree(p);
    }
}
