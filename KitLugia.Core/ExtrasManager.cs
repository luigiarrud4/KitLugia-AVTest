using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static partial class Toolbox
    {
        // =========================================================
        // 1. FERRAMENTAS DE SISTEMA (GPEDIT, GODMODE, SPOOLER)
        // =========================================================

        /// <summary>
        /// Instala o Editor de Política de Grupo (gpedit.msc) em edições do Windows que não o incluem (como Home).
        /// </summary>
        public static (bool Success, string Message) EnableGroupPolicyEditor()
        {
            string batchContent = @"
@echo off
pushd ""%~dp0""
dir /b %SystemRoot%\servicing\Packages\Microsoft-Windows-GroupPolicy-ClientExtensions-Package~3*.mum >List.txt
dir /b %SystemRoot%\servicing\Packages\Microsoft-Windows-GroupPolicy-ClientTools-Package~3*.mum >>List.txt
for /f %%i in ('findstr /i . List.txt 2^>nul') do dism /online /norestart /add-package:""%SystemRoot%\servicing\Packages\%%i""
del List.txt
";
            string tempFile = Path.Combine(Path.GetTempPath(), "gpedit_enabler.bat");
            try
            {
                File.WriteAllText(tempFile, batchContent);
                SystemUtils.RunExternalProcess(tempFile, "", hidden: false, waitForExit: false);
                return (true, "O processo de ativação do GPEDIT foi iniciado em uma nova janela.");
            }
            catch (Exception ex)
            {
                return (false, $"ERRO ao criar ou executar o script: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        public static (bool Success, string Message) ToggleGodMode()
        {
            string godModePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GodMode.{ED7BA470-8E54-465E-825C-99712043E01C}");

            try
            {
                if (Directory.Exists(godModePath))
                {
                    Directory.Delete(godModePath, true);
                    return (true, "Atalho 'God Mode' removido da Área de Trabalho.");
                }
                else
                {
                    Directory.CreateDirectory(godModePath);
                    File.SetAttributes(godModePath, FileAttributes.Directory | FileAttributes.System);
                    return (true, "Atalho 'God Mode' criado na Área de Trabalho.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"ERRO ao gerenciar o atalho: {ex.Message}");
            }
        }

        public static (bool Success, string Message) ClearPrintSpooler()
        {
            try
            {
                var stopResult = ManageService("Spooler", "stop");
                if (!stopResult.Success) return (false, "Falha ao parar o Spooler. Tente como Admin.");

                string spoolPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "PRINTERS");
                var cleanupResult = CleanDirectory(spoolPath, "Fila de Impressão");

                var startResult = ManageService("Spooler", "start");

                var message = $"Fila limpa. {cleanupResult.Message}";
                if (cleanupResult.BlockedFiles.Count > 0)
                {
                    message += $" ({cleanupResult.BlockedFiles.Count} arquivos travados)";
                }
                return (true, message);
            }
            catch (Exception ex)
            {
                return (false, $"Erro inesperado: {ex.Message}");
            }
        }

        // =========================================================
        // 2. MENU DE CONTEXTO (CLIQUE DIREITO)
        // =========================================================

        public static bool IsContextMenuItemInstalled(string keyPath)
        {
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey(keyPath);
                return key != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Adiciona ou Remove a opção "Obter Controle Total" (Take Ownership) no menu de contexto.
        /// </summary>
        public static (bool Success, string Message) ToggleTakeOwnershipContext()
        {
            const string fileKeyPath = @"*\shell\runas";
            const string dirKeyPath = @"Directory\shell\runas";

            try
            {
                if (IsContextMenuItemInstalled(fileKeyPath))
                {
                    Registry.ClassesRoot.DeleteSubKeyTree(fileKeyPath, false);
                    Registry.ClassesRoot.DeleteSubKeyTree(dirKeyPath, false);
                    return (true, "'Obter Controle Total' removido do menu.");
                }
                else
                {
                    // Para Arquivos
                    using (var fileKey = Registry.ClassesRoot.CreateSubKey(fileKeyPath))
                    {
                        fileKey.SetValue("", "Obter Controle Total");
                        fileKey.SetValue("NoWorkingDirectory", "");
                        fileKey.SetValue("HasLUAShield", ""); // Adiciona o ícone de escudo UAC
                        using var cmd = fileKey.CreateSubKey("command");
                        cmd.SetValue("", @"cmd.exe /c takeown /f ""%1"" && icacls ""%1"" /grant administradores:F");
                    }

                    // Para Pastas
                    using (var dirKey = Registry.ClassesRoot.CreateSubKey(dirKeyPath))
                    {
                        dirKey.SetValue("", "Obter Controle Total");
                        dirKey.SetValue("NoWorkingDirectory", "");
                        dirKey.SetValue("HasLUAShield", "");
                        using var cmd = dirKey.CreateSubKey("command");
                        cmd.SetValue("", @"cmd.exe /c takeown /f ""%1"" /r /d y && icacls ""%1"" /grant administradores:F /t");
                    }

                    return (true, "'Obter Controle Total' adicionado ao menu.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao modificar registro: {ex.Message}");
            }
        }

        /// <summary>
        /// Adiciona ou Remove a opção "Abrir Prompt de Comando aqui" (Admin).
        /// </summary>
        public static (bool Success, string Message) ToggleCmdContext()
        {
            const string keyPath = @"Directory\Background\shell\runascmd";
            try
            {
                if (IsContextMenuItemInstalled(keyPath))
                {
                    Registry.ClassesRoot.DeleteSubKeyTree(keyPath);
                    return (true, "Atalho CMD removido.");
                }
                else
                {
                    using var key = Registry.ClassesRoot.CreateSubKey(keyPath);
                    key.SetValue("", "Abrir CMD aqui (Admin)");
                    key.SetValue("HasLUAShield", "");
                    using var cmd = key.CreateSubKey("command");
                    cmd.SetValue("", @"powershell.exe -Command ""Start-Process cmd.exe -ArgumentList '/s /k pushd ""%V""' -Verb runas""");
                    return (true, "Atalho CMD (Admin) adicionado.");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // =========================================================
        // 3. EXPLORER TWEAKS (ARQUIVOS OCULTOS / EXTENSÕES)
        // =========================================================

        public static bool AreHiddenFilesVisible()
        {
            try
            {
                return (int)(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 2) ?? 2) == 1;
            }
            catch
            {
                return false;
            }
        }

        public static bool AreExtensionsVisible()
        {
            try
            {
                return (int)(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 1) ?? 1) == 0;
            }
            catch
            {
                return false;
            }
        }

        public static (bool Success, string Message) ToggleHiddenFiles()
        {
            try
            {
                bool current = AreHiddenFilesVisible();
                int newValue = current ? 2 : 1; // 1 = Show, 2 = Hide
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", newValue, RegistryValueKind.DWord);

                // Tenta forçar atualização visual sem matar o explorer
                RefreshExplorerSettings();

                return (true, current ? "Arquivos ocultos: ESCONDIDOS." : "Arquivos ocultos: VISÍVEIS.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static (bool Success, string Message) ToggleFileExtensions()
        {
            try
            {
                bool current = AreExtensionsVisible();
                int newValue = current ? 1 : 0; // 0 = Show, 1 = Hide (Lógica inversa do Hidden)
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", newValue, RegistryValueKind.DWord);

                RefreshExplorerSettings();

                return (true, current ? "Extensões de arquivo: ESCONDIDAS." : "Extensões de arquivo: VISÍVEIS.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // Método auxiliar para notificar o Windows que as configurações mudaram
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private static void RefreshExplorerSettings()
        {
            // 0x08000000 = SHCNE_ASSOCCHANGED (Força refresh de ícones e associações)
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }
}