using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using Microsoft.Win32;
using System.Windows.Forms;
// Resolução de Conflitos WPF vs WinForms
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace KitLugia.GUI.Pages
{
    public partial class IsoEditorPage : Page
    {
        private string _isoPath = "";
        private string _isoDestPath = "";
        private bool _isIsoEditorOperation;

        public IsoEditorPage()
        {
            InitializeComponent();
            this.Unloaded += IsoEditorPage_Unloaded;
        }

        private void IsoEditorPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        // ==========================================
        // ISO SELECTION
        // ==========================================
        private void BtnSelectIso_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "ISO Files (*.iso)|*.iso",
                Title = "Selecione a imagem ISO"
            };

            if (dlg.ShowDialog() == true)
            {
                _isoPath = dlg.FileName;
                TxtIsoPath.Text = _isoPath;
                
                // Não montamos a ISO mais - extraímos diretamente com 7-Zip
                TxtDetectedIsoType.Text = $"ISO selecionada: {Path.GetFileName(_isoPath)}";
            }
        }

        private void TxtIsoPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(TxtIsoPath.Text) && File.Exists(TxtIsoPath.Text))
            {
                _isoPath = TxtIsoPath.Text;
                TxtDetectedIsoType.Text = $"✅. ISO selecionada: {System.IO.Path.GetFileName(_isoPath)}";
            }
        }

        // ==========================================
        // CREATE BUTTON - Monta ISO e mostra configuração
        // ==========================================
        private async void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            if (_isIsoEditorOperation) return;
            _isIsoEditorOperation = true;
            try
            {
                var mw = Application.Current.MainWindow as MainWindow;
                if (mw == null) return;

                if (string.IsNullOrEmpty(_isoPath))
                {
                    mw.ShowError("ERRO", "Selecione uma imagem ISO primeiro.");
                    return;
                }

                // Não montamos a ISO mais - extraímos diretamente com 7-Zip
                // Mostra overlay de configuração diretamente
                OverlayBusy.Visibility = Visibility.Collapsed;
                OverlayConfig.Visibility = Visibility.Visible;
                TxtConfigIsoInfo.Text = $"ISO: {System.IO.Path.GetFileName(_isoPath)}\nPronta para extração e edição";
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnCreate_Click", ex.Message);
            }
            finally
            {
                _isIsoEditorOperation = false;
            }
        }

        private async void BtnConfirmStart_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;

            if (string.IsNullOrEmpty(_isoDestPath))
            {
                mw.ShowError("ERRO", "Selecione o destino da ISO.");
                return;
            }

            // Capturar valores dos checkboxes ANTES de entrar na Task.Run (evitar erro de threading)
            bool chkDebloatPreset = ChkDebloatPreset.IsChecked == true;
            bool chkInjectDrivers = ChkInjectDrivers.IsChecked == true;
            bool chkBypassRequirements = ChkBypassRequirements.IsChecked == true;
            bool chkDisableSponsoredApps = ChkDisableSponsoredApps.IsChecked == true;
            bool chkDisableTelemetry = ChkDisableTelemetry.IsChecked == true;
            bool chkDisableOneDrive = ChkDisableOneDrive.IsChecked == true;
            bool chkDisableCopilot = ChkDisableCopilot.IsChecked == true;
            bool chkDisableUpdateOOBE = ChkDisableUpdateOOBE.IsChecked == true;
            bool chkDisableTeams = ChkDisableTeams.IsChecked == true;
            bool chkDisableOutlook = ChkDisableOutlook.IsChecked == true;
            bool chkDisableBitLocker = ChkDisableBitLocker.IsChecked == true;
            bool chkDisableChat = ChkDisableChat.IsChecked == true;
            bool chkDisableReservedStorage = ChkDisableReservedStorage.IsChecked == true;
            bool chkCleanupWinSxS = ChkCleanupWinSxS.IsChecked == true;
            bool chkRemoveSupportFolder = ChkRemoveSupportFolder.IsChecked == true;

            OverlayConfig.Visibility = Visibility.Collapsed;
            OverlayBusy.Visibility = Visibility.Visible;
            TxtBusyStatus.Text = "Aplicando configurações...";

            string workDir = "";
            string isoContents = "";
            string mountDir = "";
            string driverExportDir = "";

            try
            {
                // 1. Criar diretórios de trabalho (fluxo CTT)
                TxtBusyStatus.Text = "Criando diretórios de trabalho...";
                AddLog("Criando diretórios de trabalho...");
                
                workDir = Path.Combine(Path.GetTempPath(), $"KitLugia_ISO_{DateTime.Now:yyyyMMdd_HHmmss}");
                isoContents = Path.Combine(workDir, "iso_contents");
                mountDir = Path.Combine(workDir, "wim_mount");
                
                Directory.CreateDirectory(workDir);
                Directory.CreateDirectory(isoContents);
                Directory.CreateDirectory(mountDir);
                AddLog("Diretórios de trabalho criados.");

                // 2. Extrair ISO diretamente com 7-Zip (mesma abordagem do WinbootManager)
                TxtBusyStatus.Text = "Extraindo conteúdo da ISO...";
                AddLog("Extraindo conteúdo da ISO com 7-Zip (isso pode levar alguns minutos)...");
                
                var copyResult = await CopyDirectoryAsync(isoContents);
                if (!copyResult.Success)
                {
                    OverlayBusy.Visibility = Visibility.Collapsed;
                    mw.ShowError("ERRO", $"Falha ao extrair ISO: {copyResult.Message}");
                    await CleanupIsoEdit(workDir, mountDir, _isoPath);
                    return;
                }
                AddLog("Conteúdo da ISO extraído.");

                // 3. Montar WIM com DISM
                TxtBusyStatus.Text = "Montando imagem WIM...";
                AddLog("Montando imagem WIM com DISM...");
                
                string wimPath = Path.Combine(isoContents, "sources", "install.wim");
                if (!File.Exists(wimPath))
                {
                    // Tenta install.esd
                    wimPath = Path.Combine(isoContents, "sources", "install.esd");
                }

                if (!File.Exists(wimPath))
                {
                    OverlayBusy.Visibility = Visibility.Collapsed;
                    mw.ShowError("ERRO", "install.wim ou install.esd não encontrado na ISO.");
                    await CleanupIsoEdit(workDir, mountDir, _isoPath);
                    return;
                }

                // Remove readonly do WIM
                File.SetAttributes(wimPath, FileAttributes.Normal);

                var mountResult = await IsoEditorManager.MountWim(wimPath, mountDir);
                if (!mountResult.Success)
                {
                    OverlayBusy.Visibility = Visibility.Collapsed;
                    mw.ShowError("ERRO", $"Falha ao montar WIM: {mountResult.Message}");
                    await CleanupIsoEdit(workDir, mountDir, _isoPath);
                    return;
                }
                AddLog("WIM montado com sucesso.");

                // 4. Remover AppX bloatware (CTT)
                if (chkDebloatPreset)
                {
                    AddLog("Removendo AppX bloatware...");
                    var bloatApps = new List<string>
                    {
                        "Clipchamp.Clipchamp",
                        "Microsoft.BingNews",
                        "Microsoft.BingSearch",
                        "Microsoft.BingWeather",
                        "Microsoft.GetHelp",
                        "Microsoft.MicrosoftOfficeHub",
                        "Microsoft.MicrosoftSolitaireCollection",
                        "Microsoft.MicrosoftStickyNotes",
                        "Microsoft.OutlookForWindows",
                        "Microsoft.Paint",
                        "Microsoft.PowerAutomateDesktop",
                        "Microsoft.StartExperiencesApp",
                        "Microsoft.Todos",
                        "Microsoft.Windows.DevHome",
                        "Microsoft.WindowsFeedbackHub",
                        "Microsoft.WindowsSoundRecorder",
                        "Microsoft.ZuneMusic",
                        "MicrosoftCorporationII.QuickAssist",
                        "MSTeams"
                    };
                    var debloatResult = await IsoEditorManager.RemoveProvisionedApps(mountDir, bloatApps);
                    AddLog($"Bloatware: {debloatResult.Message}");
                }

                // 5. Injetar drivers do sistema atual (CTT)
                if (chkInjectDrivers)
                {
                    AddLog("Exportando drivers do sistema atual...");
                    driverExportDir = Path.Combine(Path.GetTempPath(), $"KitLugia_DriverExport_{DateTime.Now:yyyyMMdd_HHmmss}");
                    Directory.CreateDirectory(driverExportDir);

                    var exportResult = await ExportWindowsDrivers(driverExportDir);
                    if (!exportResult.Success)
                    {
                        AddLog($"Aviso: {exportResult.Message}");
                    }
                    else
                    {
                        AddLog("Drivers exportados com sucesso.");
                        AddLog("Injetando drivers em install.wim...");
                        var injectResult = await IsoEditorManager.InjectDrivers(mountDir, driverExportDir);
                        AddLog($"Drivers injetados: {injectResult.Message}");

                        // Injetar em boot.wim também (CTT)
                        string bootWimPath = Path.Combine(isoContents, "sources", "boot.wim");
                        if (File.Exists(bootWimPath))
                        {
                            AddLog("Injetando drivers em boot.wim...");
                            await InjectBootWimDrivers(bootWimPath, driverExportDir);
                        }
                    }
                }

                // 6. Registry tweaks (CTT)
                if (chkBypassRequirements || chkDisableSponsoredApps || 
                    chkDisableTelemetry || chkDisableOneDrive ||
                    chkDisableCopilot || chkDisableUpdateOOBE ||
                    chkDisableTeams || chkDisableOutlook ||
                    chkDisableBitLocker || chkDisableChat ||
                    chkDisableReservedStorage)
                {
                    AddLog("Aplicando registry tweaks...");
                    await ApplyRegistryTweaks(mountDir, chkBypassRequirements, chkDisableSponsoredApps, 
                        chkDisableTelemetry, chkDisableOneDrive, chkDisableCopilot, chkDisableUpdateOOBE,
                        chkDisableTeams, chkDisableOutlook, chkDisableBitLocker, chkDisableChat, 
                        chkDisableReservedStorage);
                    AddLog("Registry tweaks aplicados.");
                }

                // 7. Limpar WinSxS com /ResetBase (CTT sempre usa)
                if (chkCleanupWinSxS)
                {
                    AddLog("Limpando WinSxS com /ResetBase...");
                    var cleanupResult = await IsoEditorManager.CleanupWinSxS(mountDir, true);
                    AddLog($"WinSxS: {cleanupResult.Message}");
                }

                // 8. Deletar arquivos de scheduled tasks (CTT)
                AddLog("Deletando arquivos de scheduled tasks...");
                await DeleteScheduledTaskFiles(mountDir);
                AddLog("Arquivos de scheduled tasks deletados.");

                // 9. Desmontar e commit WIM
                TxtBusyStatus.Text = "Salvando alterações...";
                AddLog("Desmontando e salvando WIM...");
                var unmountResult = await IsoEditorManager.UnmountWim(mountDir, true);
                if (!unmountResult.Success)
                {
                    OverlayBusy.Visibility = Visibility.Collapsed;
                    mw.ShowError("ERRO", $"Falha ao desmontar WIM: {unmountResult.Message}");
                    await CleanupIsoEdit(workDir, mountDir, _isoPath);
                    return;
                }
                AddLog("WIM desmontado e salvo com sucesso.");

                // 10. Remover pasta support\ (CTT)
                if (chkRemoveSupportFolder)
                {
                    AddLog("Removendo pasta support\\...");
                    string supportFolder = Path.Combine(isoContents, "support");
                    if (Directory.Exists(supportFolder))
                    {
                        try { await Task.Run(() => Directory.Delete(supportFolder, true)); AddLog("Pasta support\\ removida."); }
                        catch { AddLog("Aviso: Não foi possível remover pasta support\\."); }
                    }
                }

                // 10.5. Adicionar KitLugiaSetup à ISO (automação pós-instalação)
                TxtBusyStatus.Text = "Adicionando KitLugia à ISO...";
                AddLog("Adicionando KitLugiaSetup para automação pós-instalação...");

                string kitLugiaSetup = Path.Combine(isoContents, "_KitLugiaSetup");
                await Task.Run(() => Directory.CreateDirectory(kitLugiaSetup));

                // Copiar KitLugia.exe (do executável atual)
                try
                {
                    string kitLugiaExe = Assembly.GetExecutingAssembly().Location;
                    if (File.Exists(kitLugiaExe))
                    {
                        await Task.Run(() => File.Copy(kitLugiaExe, Path.Combine(kitLugiaSetup, "KitLugia.exe"), true));
                        AddLog("KitLugia.exe copiado para ISO.");
                    }
                    else
                    {
                        AddLog("Aviso: Não foi possível localizar KitLugia.exe para cópia.");
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"Aviso: Erro ao copiar KitLugia.exe: {ex.Message}");
                }

                // Criar config.json (configuração básica)
                string configJson = @"{
  ""AutoRun"": true,
  ""Source"": ""ISO_Automation""
}";
                await Task.Run(() => File.WriteAllText(Path.Combine(kitLugiaSetup, "config.json"), configJson));
                AddLog("config.json criado.");

                // Criar bootstrap.bat
                string bootstrapBat = @"@echo off
cd /d %~dp0
echo Verificando .NET...
dotnet --version >nul 2>&1
if %errorlevel% equ 0 (
    echo .NET ja instalado.
    goto :run_kitlugia
)
echo Instalando .NET 10...
powershell -NoProfile -ExecutionPolicy Bypass -Command ""Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile 'dotnet-install.ps1'; .\dotnet-install.ps1 -Channel 10.0 -InstallDir '%ProgramFiles%\dotnet' -InstallAsShared""
set PATH=%PATH%;%ProgramFiles%\dotnet
:run_kitlugia
echo Iniciando KitLugia...
start KitLugia.exe -Config ""config.json"" -Run
exit
";
                await Task.Run(() => File.WriteAllText(Path.Combine(kitLugiaSetup, "bootstrap.bat"), bootstrapBat));
                AddLog("bootstrap.bat criado.");

                // Criar first_logon.bat
                string firstLogonBat = @"@echo off
for %%i in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
    if exist %%i:\_KitLugiaSetup\bootstrap.bat (
        echo Executando KitLugiaSetup de %%i:\
        call %%i:\_KitLugiaSetup\bootstrap.bat
        exit
    )
)
echo KitLugiaSetup nao encontrado.
exit
";
                await Task.Run(() => File.WriteAllText(Path.Combine(kitLugiaSetup, "first_logon.bat"), firstLogonBat));
                AddLog("first_logon.bat criado.");

                // 10.6. Criar arquivo de identificação .kitlugia
                string kitlugiaId = @"# KitLugia ISO Identifier
# Esta ISO foi criada pelo KitLugia ISO Editor
# WinBoot deve preservar o autounattend.xml existente
# Apenas modificar nome de usuário e conta local se necessário

KitLugiaISO=true
Version=1.0
Created=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + @"
Source=KitLugia_ISO_Editor
PreserveAutounattend=true
AllowUserConfig=true
";
                await Task.Run(() => File.WriteAllText(Path.Combine(isoContents, ".kitlugia"), kitlugiaId));
                AddLog("Arquivo de identificação .kitlugia criado.");

                // 11. Criar ISO final (não precisa desmontar ISO original já que não montamos)
                TxtBusyStatus.Text = "Criando ISO final...";
                AddLog("Criando ISO final...");
                var createResult = await IsoEditorManager.CreateIso(isoContents, _isoDestPath);
                if (createResult.Success)
                {
                    OverlayBusy.Visibility = Visibility.Collapsed;
                    TxtStatus.Text = "✅. ISO criada com sucesso!";
                    AddLog($"ISO criada com sucesso em: {_isoDestPath}");
                    mw.ShowSuccess("SUCESSO", $"ISO criada com sucesso!\nDestino: {_isoDestPath}");
                    
                    // Limpar diretórios temporários em background
                    await Task.Run(() =>
                    {
                        try { Directory.Delete(workDir, true); } catch { }
                        try { Directory.Delete(driverExportDir, true); } catch { }
                    });
                }
                else
                {
                    OverlayBusy.Visibility = Visibility.Collapsed;
                    mw.ShowError("ERRO", createResult.Message);
                }
            }
            catch (Exception ex)
            {
                OverlayBusy.Visibility = Visibility.Collapsed;
                mw.ShowError("ERRO", ex.Message);
                await CleanupIsoEdit(workDir, mountDir, _isoPath);
            }
        }

        private async Task<(bool Success, string Message)> ExportWindowsDrivers(string destDir)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Export-WindowsDriver -Online -Destination '{destDir}'\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (process.ExitCode == 0)
                        return (true, "Drivers exportados com sucesso.");
                    else
                        return (false, $"Erro ao exportar drivers: {error}");
                }
                catch (Exception ex)
                {
                    return (false, $"Exceção ao exportar drivers: {ex.Message}");
                }
            });
        }

        private async Task InjectBootWimDrivers(string bootWimPath, string driverDir)
        {
            await Task.Run(async () =>
            {
                try
                {
                    string bootMountDir = Path.Combine(Path.GetTempPath(), $"KitLugia_BootMount_{DateTime.Now:yyyyMMdd_HHmmss}");
                    Directory.CreateDirectory(bootMountDir);

                    File.SetAttributes(bootWimPath, FileAttributes.Normal);

                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/Mount-Image /ImageFile:\"{bootWimPath}\" /Index:2 /MountDir:\"{bootMountDir}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var mountProcess = Process.Start(psi)!;
                    await mountProcess.WaitForExitAsync();

                    if (mountProcess.ExitCode == 0)
                    {
                        var injectPsi = new ProcessStartInfo
                        {
                            FileName = "dism.exe",
                            Arguments = $"/Image:\"{bootMountDir}\" /Add-Driver /Driver:\"{driverDir}\" /Recurse",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        using var injectProcess = Process.Start(injectPsi)!;
                        await injectProcess.WaitForExitAsync();

                        var unmountPsi = new ProcessStartInfo
                        {
                            FileName = "dism.exe",
                            Arguments = $"/Unmount-Image /MountDir:\"{bootMountDir}\" /Save",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        using var unmountProcess = Process.Start(unmountPsi)!;
                        await unmountProcess.WaitForExitAsync();
                    }

                    try { Directory.Delete(bootMountDir, true); } catch { }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao injetar drivers no boot.wim: {ex.Message}");
                }
            });
        }

        private async Task ApplyRegistryTweaks(string mountDir, bool bypassRequirements, bool disableSponsoredApps, 
            bool disableTelemetry, bool disableOneDrive, bool disableCopilot, bool disableUpdateOOBE,
            bool disableTeams, bool disableOutlook, bool disableBitLocker, bool disableChat, 
            bool disableReservedStorage)
        {
            await Task.Run(async () =>
            {
                try
                {
                    // Carregar hives offline
                    string componentsPath = Path.Combine(mountDir, "Windows", "System32", "config", "COMPONENTS");
                    string defaultPath = Path.Combine(mountDir, "Windows", "System32", "config", "default");
                    string ntuserPath = Path.Combine(mountDir, "Users", "Default", "ntuser.dat");
                    string softwarePath = Path.Combine(mountDir, "Windows", "System32", "config", "SOFTWARE");
                    string systemPath = Path.Combine(mountDir, "Windows", "System32", "config", "SYSTEM");

                    var psi = new ProcessStartInfo
                    {
                        FileName = "reg.exe",
                        Arguments = $"load HKLM\\zCOMPONENTS \"{componentsPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    await (Process.Start(psi)?.WaitForExitAsync() ?? Task.CompletedTask);

                    psi.Arguments = $"load HKLM\\zDEFAULT \"{defaultPath}\"";
                    await (Process.Start(psi)?.WaitForExitAsync() ?? Task.CompletedTask);

                    psi.Arguments = $"load HKLM\\zNTUSER \"{ntuserPath}\"";
                    await (Process.Start(psi)?.WaitForExitAsync() ?? Task.CompletedTask);

                    psi.Arguments = $"load HKLM\\zSOFTWARE \"{softwarePath}\"";
                    await (Process.Start(psi)?.WaitForExitAsync() ?? Task.CompletedTask);

                    psi.Arguments = $"load HKLM\\zSYSTEM \"{systemPath}\"";
                    await (Process.Start(psi)?.WaitForExitAsync() ?? Task.CompletedTask);

                    // Aplicar tweaks baseados nos parâmetros
                    if (bypassRequirements)
                    {
                        // Bypass TPM, CPU, RAM, Secure Boot, Storage
                        await ExecuteRegAdd("HKLM\\zSYSTEM\\Setup\\LabConfig", "BypassCPUCheck", "REG_DWORD", "1");
                        await ExecuteRegAdd("HKLM\\zSYSTEM\\Setup\\LabConfig", "BypassRAMCheck", "REG_DWORD", "1");
                        await ExecuteRegAdd("HKLM\\zSYSTEM\\Setup\\LabConfig", "BypassSecureBootCheck", "REG_DWORD", "1");
                        await ExecuteRegAdd("HKLM\\zSYSTEM\\Setup\\LabConfig", "BypassStorageCheck", "REG_DWORD", "1");
                        await ExecuteRegAdd("HKLM\\zSYSTEM\\Setup\\LabConfig", "BypassTPMCheck", "REG_DWORD", "1");
                        await ExecuteRegAdd("HKLM\\zSYSTEM\\Setup\\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU", "REG_DWORD", "1");
                    }

                    if (disableSponsoredApps)
                    {
                        // Desabilitar apps patrocinados
                        await ExecuteRegAdd("HKLM\\zNTUSER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "OemPreInstalledAppsEnabled", "REG_DWORD", "0");
                        await ExecuteRegAdd("HKLM\\zNTUSER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "PreInstalledAppsEnabled", "REG_DWORD", "0");
                        await ExecuteRegAdd("HKLM\\zNTUSER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "SilentInstalledAppsEnabled", "REG_DWORD", "0");
                        await ExecuteRegAdd("HKLM\\zSOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsConsumerFeatures", "REG_DWORD", "1");
                    }

                    if (disableTelemetry)
                    {
                        // Desabilitar telemetria
                        await ExecuteRegAdd("HKLM\\zNTUSER\\Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo", "Enabled", "REG_DWORD", "0");
                        await ExecuteRegAdd("HKLM\\zSOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "AllowTelemetry", "REG_DWORD", "0");
                    }

                    if (disableOneDrive)
                    {
                        // Desabilitar OneDrive
                        await ExecuteRegAdd("HKLM\\zSOFTWARE\\Policies\\Microsoft\\Windows\\OneDrive", "DisableFileSyncNGSC", "REG_DWORD", "1");
                    }

                    if (disableCopilot)
                    {
                        // Desabilitar Copilot
                        await ExecuteRegAdd("HKLM\\zSOFTWARE\\Policies\\Microsoft\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot", "REG_DWORD", "1");
                    }

                    if (disableUpdateOOBE)
                    {
                        // Desabilitar Windows Update no OOBE
                        await ExecuteRegAdd("HKLM\\zSOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU", "NoAutoUpdate", "REG_DWORD", "1");
                        await ExecuteRegAdd("HKLM\\zSOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate", "DisableWindowsUpdateAccess", "REG_DWORD", "1");
                    }

                    if (disableTeams)
                    {
                        // Prevenir Teams
                        await ExecuteRegAdd("HKLM\\zSOFTWARE\\Policies\\Microsoft\\Teams", "DisableInstallation", "REG_DWORD", "1");
                    }

                    if (disableOutlook)
                    {
                        // Prevenir novo Outlook
                        await ExecuteRegAdd("HKLM\\zSOFTWARE\\Policies\\Microsoft\\Windows\\Windows Mail", "PreventRun", "REG_DWORD", "1");
                    }

                    if (disableBitLocker)
                    {
                        // Desabilitar BitLocker
                        await ExecuteRegAdd("HKLM\\zSYSTEM\\ControlSet001\\Control\\BitLocker", "PreventDeviceEncryption", "REG_DWORD", "1");
                    }

                    if (disableChat)
                    {
                        // Desabilitar Chat icon
                        await ExecuteRegAdd("HKLM\\zSOFTWARE\\Policies\\Microsoft\\Windows\\Windows Chat", "ChatIcon", "REG_DWORD", "3");
                    }

                    if (disableReservedStorage)
                    {
                        // Desabilitar Reserved Storage
                        await ExecuteRegAdd("HKLM\\zSOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ReserveManager", "ShippedWithReserves", "REG_DWORD", "0");
                    }

                    // Descarregar hives
                    psi.Arguments = "unload HKLM\\zCOMPONENTS";
                    await (Process.Start(psi)?.WaitForExitAsync() ?? Task.CompletedTask);

                    psi.Arguments = "unload HKLM\\zDEFAULT";
                    await (Process.Start(psi)?.WaitForExitAsync() ?? Task.CompletedTask);

                    psi.Arguments = "unload HKLM\\zNTUSER";
                    await (Process.Start(psi)?.WaitForExitAsync() ?? Task.CompletedTask);

                    psi.Arguments = "unload HKLM\\zSOFTWARE";
                    await (Process.Start(psi)?.WaitForExitAsync() ?? Task.CompletedTask);

                    psi.Arguments = "unload HKLM\\zSYSTEM";
                    await (Process.Start(psi)?.WaitForExitAsync() ?? Task.CompletedTask);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao aplicar registry tweaks: {ex.Message}");
                }
            });
        }

        private async Task ExecuteRegAdd(string key, string valueName, string type, string value)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "reg.exe",
                    Arguments = $"add \"{key}\" /v {valueName} /t {type} /d {value} /f",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                await (Process.Start(psi)?.WaitForExitAsync() ?? Task.CompletedTask);
            }
            catch { }
        }

        private async Task DeleteScheduledTaskFiles(string mountDir)
        {
            await Task.Run(() =>
            {
                try
                {
                    string tasksPath = Path.Combine(mountDir, "Windows", "System32", "Tasks");
                    string[] tasksToDelete =
                    {
                        "Microsoft\\Windows\\Application Experience\\Microsoft Compatibility Appraiser",
                        "Microsoft\\Windows\\Customer Experience Improvement Program",
                        "Microsoft\\Windows\\Application Experience\\ProgramDataUpdater",
                        "Microsoft\\Windows\\Chkdsk\\Proxy",
                        "Microsoft\\Windows\\Windows Error Reporting\\QueueReporting",
                        "Microsoft\\Windows\\InstallService",
                        "Microsoft\\Windows\\UpdateOrchestrator",
                        "Microsoft\\Windows\\UpdateAssistant",
                        "Microsoft\\Windows\\WaaSMedic",
                        "Microsoft\\Windows\\WindowsUpdate"
                    };

                    foreach (string task in tasksToDelete)
                    {
                        string taskPath = Path.Combine(tasksPath, task);
                        if (Directory.Exists(taskPath))
                        {
                            try { Directory.Delete(taskPath, true); }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao deletar arquivos de scheduled tasks: {ex.Message}");
                }
            });
        }

        private async Task<(bool Success, string Message)> CopyDirectoryAsync(string destDir)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    // Usar 7-Zip do resources para extrair diretamente do arquivo ISO (mesma abordagem do WinbootManager)
                    string sevenZipPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "App", "7Zip", "7z.exe");
                    
                    if (!File.Exists(sevenZipPath))
                    {
                        return (false, $"7-Zip não encontrado em {sevenZipPath}");
                    }

                    // Extração com 7-Zip (mesma abordagem do WinbootManager.ExtractFiles)
                    // Extrai diretamente do arquivo ISO ao invés do drive montado
                    string args = $"x \"{_isoPath}\" -o\"{destDir}\" -y";
                    
                    var (extCode, extOut) = await ExecuteShellCommand(sevenZipPath, args);
                    
                    // 7-Zip return codes: 0 = No error, 1 = Warning (Non fatal errors)
                    if (extCode != 0 && extCode != 1) 
                    {
                        return (false, $"Erro 7-Zip (Código {extCode}): {extOut}");
                    }

                    return (true, "ISO extraída com 7-Zip com sucesso.");
                }
                catch (Exception ex)
                {
                    return (false, $"Exceção ao extrair ISO: {ex.Message}");
                }
            });
        }

        private async Task<(int exitCode, string output)> ExecuteShellCommand(string filename, string args)
        {
            return await Task.Run(async () =>
            {
                var psi = new ProcessStartInfo(filename, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi)!;
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                return (process.ExitCode, output + (string.IsNullOrEmpty(error) ? "" : $"\n[ERROR]: {error}"));
            });
        }

        private async Task CleanupIsoEdit(string workDir, string mountDir, string isoPath)
        {
            try
            {
                // Tentar desmontar WIM se montado
                if (!string.IsNullOrEmpty(mountDir) && Directory.Exists(mountDir))
                {
                    await IsoEditorManager.UnmountWim(mountDir, false);
                }

                // Não desmontamos a ISO mais - extraímos diretamente com 7-Zip

                // Limpar diretórios temporários
                if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir))
                {
                    try
                    {
                        Directory.Delete(workDir, true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void AddLog(string message)
        {
            TxtLogViewer.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            TxtLogViewer.ScrollToEnd();
        }

        // ==========================================
        // BASIC OPERATIONS (mantidos para compatibilidade com overlay)
        // ==========================================
        private void BtnSelectIsoDest_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "ISO Files (*.iso)|*.iso",
                DefaultExt = "iso",
                FileName = "KitLugia_Custom_Windows.iso"
            };

            if (dlg.ShowDialog() == true)
            {
                _isoDestPath = dlg.FileName;
                TxtIsoDest.Text = _isoDestPath;
            }
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            ChkInjectDrivers.IsChecked = true;
            ChkDebloatPreset.IsChecked = true;
            ChkBypassRequirements.IsChecked = true;
            ChkDisableSponsoredApps.IsChecked = true;
            ChkDisableTelemetry.IsChecked = true;
            ChkDisableOneDrive.IsChecked = true;
            ChkDisableCopilot.IsChecked = true;
            ChkDisableUpdateOOBE.IsChecked = true;
            ChkDisableTeams.IsChecked = true;
            ChkDisableOutlook.IsChecked = true;
            ChkDisableBitLocker.IsChecked = true;
            ChkDisableChat.IsChecked = true;
            ChkDisableReservedStorage.IsChecked = true;
            ChkCleanupWinSxS.IsChecked = false; // NfO marcar /ResetBase por padrão (causa travamentos)
            ChkRemoveSupportFolder.IsChecked = true;
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            ChkInjectDrivers.IsChecked = false;
            ChkDebloatPreset.IsChecked = false;
            ChkBypassRequirements.IsChecked = false;
            ChkDisableSponsoredApps.IsChecked = false;
            ChkDisableTelemetry.IsChecked = false;
            ChkDisableOneDrive.IsChecked = false;
            ChkDisableCopilot.IsChecked = false;
            ChkDisableUpdateOOBE.IsChecked = false;
            ChkDisableTeams.IsChecked = false;
            ChkDisableOutlook.IsChecked = false;
            ChkDisableBitLocker.IsChecked = false;
            ChkDisableChat.IsChecked = false;
            ChkDisableReservedStorage.IsChecked = false;
            ChkCleanupWinSxS.IsChecked = false;
            ChkRemoveSupportFolder.IsChecked = false;
        }

        private async void BtnCleanup_Click(object sender, RoutedEventArgs e)
        {
            if (_isIsoEditorOperation) return;
            _isIsoEditorOperation = true;
            try
            {
                var mw = Application.Current.MainWindow as MainWindow;
                if (mw == null) return;

                OverlayBusy.Visibility = Visibility.Visible;
                TxtBusyStatus.Text = "Limpando lixo do DISM/WIM...";

                var result = await IsoManager.CleanupDismWim();

                OverlayBusy.Visibility = Visibility.Collapsed;

                if (result.Success)
                {
                    mw.ShowInfo("Limpeza Concluída", result.Message);
                }
                else
                {
                    mw.ShowError("Erro na Limpeza", result.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnCleanup_Click", ex.Message);
            }
            finally
            {
                _isIsoEditorOperation = false;
            }
        }

        private void BtnCancelConfig_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            mw?.NavigateToPage(PageType.AdvancedTools);
        }

        public void Cleanup()
        {
            _isoDestPath = string.Empty;
            this.Unloaded -= IsoEditorPage_Unloaded;
            this.DataContext = null;
        }
    }
}
