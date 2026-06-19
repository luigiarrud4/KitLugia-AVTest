using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using Microsoft.Win32;

namespace KitLugia.SpoofExtension.Pages
{
    public partial class SpoofsPage : Page
    {
        public SpoofsPage()
        {
            InitializeComponent();
            _ = LoadVolumeSerialAsync();

            System.Windows.MessageBox.Show(
                "⚠️  SPOOF EXTRAS — ÁREA AVANÇADA\n\n" +
                "Estas ferramentas modificam identificadores do seu sistema para bypass de bloqueios por hardware (HWID ban).\n\n" +
                "✅  O KitLugia NÃO altera seu hardware real.\n" +
                "Todas as alterações são em nível de SOFTWARE (registro / setor de boot).\n" +
                "Nada é gravado na firmware do dispositivo.\n\n" +
                "❌  Anti-cheats kernel-mode (EAC, BattlEye, Vanguard) operam em ring-0\n" +
                "e AINDA PODEM LER seus identificadores reais mesmo após o spoof.\n" +
                "Para bypass completo de kernel driver, é necessário um driver kernel dedicado.\n\n" +
                "⚠️  Volume Serial patcha o setor de boot — risco pequeno mas real.\n" +
                "Recomendado: crie um restore point antes de usar.\n\n" +
                "Ao continuar, você confirma que entende os riscos e limitações.",
                "KitLugia — Spoof Extras",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }

        // =================================================================
        // 1. OUI MIRROR MODE
        // =================================================================
        private async void BtnOuiMirrorGenerate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var phys = AdapterManager.ListPhysicalAdapters().Where(a => a.SupportsSpoofing).ToList();
                if (phys.Count == 0) { TxtOuiMirrorStatus.Text = "Nenhum adaptador com suporte a spoofing."; return; }

                int ok = 0, fail = 0;
                foreach (var ad in phys)
                {
                    try
                    {
                        string current = AdapterManager.GetCurrentMac(ad.ConnectionName);
                        if (string.IsNullOrEmpty(current) || current == "00-00-00-00-00-00") continue;

                        string mirrored = AdapterManager.GenerateMirroredMac(current);
                        var result = AdapterManager.SetMacAddress(ad.Id, mirrored);
                        if (result.Success)
                        {
                            await Task.Delay(1000);
                            AdapterManager.RestartAdapter(ad.ConnectionName);
                            ok++;
                        }
                        else fail++;
                    }
                    catch { fail++; }
                }

                TxtOuiMirrorStatus.Text = ok > 0
                    ? $"{ok} adaptador(es) com OUI mirror aplicado." + (fail > 0 ? $" {fail} falha(s)." : "")
                    : "Falha ao aplicar OUI mirror em todos os adaptadores.";
            }
            catch (Exception ex)
            {
                TxtOuiMirrorStatus.Text = $"Erro: {ex.Message}";
            }
        }

        private async void BtnOuiMirrorRestore_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var phys = AdapterManager.ListPhysicalAdapters().Where(a => a.SupportsSpoofing).ToList();
                if (phys.Count == 0) { TxtOuiMirrorStatus.Text = "Nenhum adaptador com suporte a spoofing."; return; }

                int ok = 0, fail = 0;
                foreach (var ad in phys)
                {
                    try
                    {
                        var result = AdapterManager.RestoreOriginalMac(ad.Id, ad.NetCfgInstanceId, ad.ConnectionName);
                        if (result.Success)
                        {
                            await Task.Delay(1000);
                            AdapterManager.RestartAdapter(ad.ConnectionName);
                            ok++;
                        }
                        else fail++;
                    }
                    catch { fail++; }
                }

                TxtOuiMirrorStatus.Text = ok > 0
                    ? $"{ok} adaptador(es) restaurado(s)." + (fail > 0 ? $" {fail} falha(s)." : "")
                    : "Falha ao restaurar.";
            }
            catch (Exception ex)
            {
                TxtOuiMirrorStatus.Text = $"Erro: {ex.Message}";
            }
        }

        // =================================================================
        // 2. DHCP REFRESH
        // =================================================================
        private async void BtnDhcpRefresh_Click(object sender, RoutedEventArgs e)
        {
            TxtDhcpRefreshStatus.Text = "Liberando IP...";
            try
            {
                await Task.Run(() =>
                {
                    using var p = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo("ipconfig", "/release")
                        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }
                    };
                    p.Start(); p.WaitForExit(10000);
                });

                await Task.Delay(2000);
                TxtDhcpRefreshStatus.Text = "Renovando IP...";

                await Task.Run(() =>
                {
                    using var p = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo("ipconfig", "/renew")
                        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }
                    };
                    p.Start(); p.WaitForExit(30000);
                });

                TxtDhcpRefreshStatus.Text = "IP renovado com sucesso!";
            }
            catch (Exception ex)
            {
                TxtDhcpRefreshStatus.Text = $"Erro: {ex.Message}";
            }
        }

        // =================================================================
        // 3. MAC ROTATION (todos os adaptadores)
        // =================================================================
        private async void BtnRotateAllMacs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var phys = AdapterManager.ListPhysicalAdapters().Where(a => a.SupportsSpoofing).ToList();
                if (phys.Count == 0) { TxtMacRotationStatus.Text = "Nenhum adaptador com suporte a spoofing."; return; }

                int ok = 0, fail = 0;
                foreach (var ad in phys)
                {
                    try
                    {
                        string newMac = AdapterManager.GenerateRandomMac();
                        var result = AdapterManager.SetMacAddress(ad.Id, newMac);
                        if (result.Success)
                        {
                            await Task.Delay(1000);
                            AdapterManager.RestartAdapter(ad.ConnectionName);
                            ok++;
                        }
                        else fail++;
                    }
                    catch { fail++; }
                }

                TxtMacRotationStatus.Text = ok > 0
                    ? $"MAC rotacionado em {ok} adaptador(es)." + (fail > 0 ? $" {fail} falha(s)." : "")
                    : "Falha ao rotacionar MAC.";
            }
            catch (Exception ex)
            {
                TxtMacRotationStatus.Text = $"Erro: {ex.Message}";
            }
        }

        // =================================================================
        // 4. HARDWARE IDS (ring-3)
        // =================================================================
        private void BtnRotateHwids_Click(object sender, RoutedEventArgs e)
        {
            var result = SpoofsManager.RotateHardwareIds();
            HwidResultsBox.Visibility = Visibility.Visible;
            TxtHwidResults.Text = string.Join("\n", result.Details) +
                $"\n\n{result.Count} identificadores alterados. Reinicie para aplicar.";
        }

        private void BtnShowCurrentHwids_Click(object sender, RoutedEventArgs e)
        {
            var lines = new List<string>();
            void ReadGuid(string path, string name)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path);
                    if (key?.GetValue(name) is string val)
                        lines.Add($"{name}: {val[..8]}...");
                    else
                        lines.Add($"{name}: (nao encontrado)");
                }
                catch { lines.Add($"{name}: (erro ao ler)"); }
            }

            ReadGuid(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid");
            ReadGuid(@"SYSTEM\CurrentControlSet\Control\IDConfigDB\Hardware Profiles\0001", "HwProfileGuid");
            ReadGuid(@"SOFTWARE\Microsoft\SQMClient", "MachineId");
            ReadGuid(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate", "SusClientId");
            void ReadString(string path, string name)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path);
                    if (key?.GetValue(name) is string val)
                        lines.Add($"{name}: {(val.Length > 8 ? val[..8] + "..." : val)}");
                    else
                        lines.Add($"{name}: (nao encontrado)");
                }
                catch { lines.Add($"{name}: (erro ao ler)"); }
            }
            ReadString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductId");
            ReadString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallDate");

            HwidResultsBox.Visibility = Visibility.Visible;
            TxtHwidResults.Text = string.Join("\n", lines);
        }

        // =================================================================
        // 5. DISK VOLUME SERIAL
        // =================================================================
        private async Task LoadVolumeSerialAsync()
        {
            try
            {
                var serial = await Task.Run(() =>
                {
                    using var mo = new ManagementObject($"Win32_LogicalDisk.DeviceID=\"C:\"");
                    mo.Get();
                    return mo["VolumeSerialNumber"]?.ToString() ?? "---";
                });
                TxtCurrentVolumeSerial.Text = serial;
            }
            catch { TxtCurrentVolumeSerial.Text = "---"; }
        }

        private async void BtnPatchVolumeSerial_Click(object sender, RoutedEventArgs e)
        {
            TxtVolumeSerialStatus.Text = "Aplicando patch no setor de boot...";
            try
            {
                uint newSerial = (uint)Random.Shared.Next();
                var result = await Task.Run(() => SpoofsManager.PatchVolumeSerial('C', newSerial));

                TxtVolumeSerialStatus.Text = result.Success
                    ? result.Message
                    : $"Falha: {result.Message}";

                if (result.Success)
                    await LoadVolumeSerialAsync();
            }
            catch (Exception ex)
            {
                TxtVolumeSerialStatus.Text = $"Erro: {ex.Message}";
            }
        }

        private async void BtnRefreshVolumeSerial_Click(object sender, RoutedEventArgs e)
        {
            await LoadVolumeSerialAsync();
        }

        // =================================================================
        // 6. SPOOF ALL (TraceX-style)
        // =================================================================
        private async void BtnSpoofAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var allDetails = new List<string>();
                int total = 0;

                var (c1, d1) = await Task.Run(() => SpoofsManager.SpoofAll());
                total += c1;
                allDetails.Add(d1);

                allDetails.Add("\n=== MAC Rotation (todos adaptadores físicos) ===");
                try
                {
                    var adapters = AdapterManager.ListPhysicalAdapters().Where(a => a.SupportsSpoofing).ToList();
                    int macOk = 0, macFail = 0;
                    foreach (var ad in adapters)
                    {
                        string newMac = AdapterManager.GenerateRandomMac();
                        var macResult = AdapterManager.SetMacAddress(ad.Id, newMac);
                        if (macResult.Success) { macOk++; total++; }
                        else macFail++;
                    }
                    allDetails.Add($"{macOk} adaptadores com MAC rotacionado" + (macFail > 0 ? $", {macFail} falha(s)" : ""));
                }
                catch (Exception ex) { allDetails.Add($"MAC rotation falhou: {ex.Message}"); }

                SpoofAllResultsBox.Visibility = Visibility.Visible;
                TxtSpoofAllResults.Text = string.Join("\n", allDetails) +
                    $"\n\n=== Total: {total} alterações feitas. Reinicie para aplicar tudo. ===";
            }
            catch (Exception ex)
            {
                SpoofAllResultsBox.Visibility = Visibility.Visible;
                TxtSpoofAllResults.Text = $"Erro: {ex.Message}";
            }
        }

        private void BtnSpoofAllLog_Click(object sender, RoutedEventArgs e)
        {
            SpoofAllResultsBox.Visibility = SpoofAllResultsBox.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        // =================================================================
        // 7. GPU SPOOF
        // =================================================================
        private async void BtnSpoofGpu_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = await Task.Run(() => SpoofsManager.SpoofGpuIdentifiers());
                GpuResultsBox.Visibility = Visibility.Visible;
                TxtGpuResults.Text = string.Join("\n", result.Details) + $"\n\n{result.Count} alterações na GPU.";
            }
            catch (Exception ex)
            {
                GpuResultsBox.Visibility = Visibility.Visible;
                TxtGpuResults.Text = $"Erro: {ex.Message}";
            }
        }

        private void BtnShowGpuLog_Click(object sender, RoutedEventArgs e)
        {
            GpuResultsBox.Visibility = GpuResultsBox.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        // =================================================================
        // 8. SMBIOS REGISTRY SPOOF
        // =================================================================
        private async void BtnSpoofSmbios_Click(object sender, RoutedEventArgs e)
        {
            TxtSmbiosStatus.Text = "Aplicando spoof SMBIOS...";
            try
            {
                var result = await Task.Run(() => SpoofsManager.SpoofSmbiosRegistry());
                TxtSmbiosStatus.Text = $"{result.Count} valores alterados. Reinicie para verificar.";
            }
            catch (Exception ex)
            {
                TxtSmbiosStatus.Text = $"Erro: {ex.Message}";
            }
        }

        // =================================================================
        // 9. DISK SERIALS (registry cache)
        // =================================================================
        private async void BtnSpoofDiskSerials_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = await Task.Run(() => SpoofsManager.SpoofDiskSerials());
                DiskSerialsResultsBox.Visibility = Visibility.Visible;
                TxtDiskSerialsResults.Text = string.Join("\n", result.Details) +
                    $"\n\n{result.Count} seriais de disco alterados no registro.";
            }
            catch (Exception ex)
            {
                DiskSerialsResultsBox.Visibility = Visibility.Visible;
                TxtDiskSerialsResults.Text = $"Erro: {ex.Message}";
            }
        }

        // =================================================================
        // 10. USB/HID DEVICE SERIALS
        // =================================================================
        private async void BtnSpoofUsbSerials_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = await Task.Run(() => SpoofsManager.SpoofUsbSerials());
                UsbSerialsResultsBox.Visibility = Visibility.Visible;
                TxtUsbSerialsResults.Text = string.Join("\n", result.Details) +
                    $"\n\n{result.Count} seriais USB/HID alterados no registro.";
            }
            catch (Exception ex)
            {
                UsbSerialsResultsBox.Visibility = Visibility.Visible;
                TxtUsbSerialsResults.Text = $"Erro: {ex.Message}";
            }
        }

        // =================================================================
        // 11. SYSTEM INFORMATION
        // =================================================================
        private async void BtnSpoofSysInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = await Task.Run(() => SpoofsManager.SpoofSystemInformation());
                SysInfoResultsBox.Visibility = Visibility.Visible;
                TxtSysInfoResults.Text = string.Join("\n", result.Details) +
                    $"\n\n{result.Count} valores de sistema alterados.";
            }
            catch (Exception ex)
            {
                SysInfoResultsBox.Visibility = Visibility.Visible;
                TxtSysInfoResults.Text = $"Erro: {ex.Message}";
            }
        }

        // =================================================================
        // 12. REGISTRY TRACE CLEANER
        // =================================================================
        private async void BtnCleanTraces_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = await Task.Run(() => SpoofsManager.CleanRegistryTraces());
                TraceCleanerResultsBox.Visibility = Visibility.Visible;
                TxtTraceCleanerResults.Text = string.Join("\n", result.Details) +
                    $"\n\n{result.Count} traces removidos.";
            }
            catch (Exception ex)
            {
                TraceCleanerResultsBox.Visibility = Visibility.Visible;
                TxtTraceCleanerResults.Text = $"Erro: {ex.Message}";
            }
        }

        // =================================================================
        // 13. RESTORE ALL (apenas MAC)
        // =================================================================
        private async void BtnRestoreAll_Click(object sender, RoutedEventArgs e)
        {
            var confirm = System.Windows.MessageBox.Show(
                "Isso vai restaurar o MAC ORIGINAL de TODOS os adaptadores de rede.\n\n" +
                "As alterações de HWIDs, Volume Serial, GPU, SMBIOS, Disk Serials e System Info NÃO serão revertidas.\n\n" +
                "Deseja continuar?",
                "Restaurar MAC Original",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                var phys = AdapterManager.ListPhysicalAdapters().Where(a => a.SupportsSpoofing).ToList();
                if (phys.Count == 0) { TxtRestoreAllStatus.Text = "Nenhum adaptador com suporte a spoofing."; return; }

                int ok = 0, fail = 0;
                foreach (var ad in phys)
                {
                    try
                    {
                        var result = AdapterManager.RestoreOriginalMac(ad.Id, ad.NetCfgInstanceId, ad.ConnectionName);
                        if (result.Success)
                        {
                            await Task.Delay(1000);
                            AdapterManager.RestartAdapter(ad.ConnectionName);
                            ok++;
                        }
                        else fail++;
                    }
                    catch { fail++; }
                }

                TxtRestoreAllStatus.Text = ok > 0
                    ? $"{ok} adaptador(es) restaurado(s) para MAC original." + (fail > 0 ? $" {fail} falha(s)." : "")
                    : "Falha ao restaurar.";
            }
            catch (Exception ex)
            {
                TxtRestoreAllStatus.Text = $"Erro: {ex.Message}";
            }
        }
    }
}
