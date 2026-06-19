using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using KitLugia.GUI.Extensions;
using KitLugia.GUI.Helpers;
using KitLugia.GUI.Pages;
using KitLugia.GUI.Services;
// Resolve ambiguidade
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using MainWindow = KitLugia.GUI.MainWindow;
namespace KitLugia.GUI.Pages
{
    public partial class DashboardPage : Page
    {
        private List<CustomMotorProfile> _customProfiles = new();
        private readonly string _profilesFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia",
            "custom_profiles.json");

        public DashboardPage()
        {
            InitializeComponent();
            this.Loaded += DashboardPage_Loaded;
            this.Unloaded += DashboardPage_Unloaded;
        }

        private async void DashboardPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Pequeno delay para não competir com a animação de intro
            try { await Task.Delay(80); } catch { }
            _ = LoadSystemInfo();
        }


        public void Cleanup()
        {

            this.Unloaded -= DashboardPage_Unloaded;


            this.DataContext = null;




            MemoryHelper.TrimWorkingSet();
        }

        private void DashboardPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }
       
        private async Task LoadSystemInfo()
        {
            try
            {
                TxtPCName.Text = System.Environment.MachineName;

                // WMI síncrono em background para não travar a UI thread
                double ram = await Task.Run(() => SystemUtils.GetTotalSystemRamGB());

                using var dashManager = new DashboardManager();
                var snapshot = await dashManager.GetSystemSnapshotAsync();

                TxtSpecs.Text = $"{ram:F0} GB de RAM • {snapshot.OsName} • {snapshot.CpuName} • {snapshot.GpuName}";
            }
            catch
            {
                TxtSpecs.Text = "Falha ao ler hardware.";
            }
        }

        // === PERFORMANCE E OTIMIZACAO ===
        private void BtnGoToTweaks_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Tweaks);
        private void BtnGoToCleanup_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Storage);
        private void BtnGoToBloatware_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Apps);
        private void BtnGoToGameBoost_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.GameBoost);
        private void BtnGoToGames_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Games);
        private void BtnGoToWinTune_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.WinTune);
        private void BtnGoToShrink_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Shrink);
        private void BtnGoToTools_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Tools);
        private void BtnGoToPowerPlans_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Tools, 0);

        // === REDE E INTERNET ===
        private void BtnGoToNetwork_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Network);
        
        // === PRIVACIDADE E SEGURANCA ===
        private void BtnGoToPrivacy_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Privacy);
        private void BtnGoToSecurity_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Security);
        private void BtnGoToIntegrity_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Integrity);

        // === SISTEMA E REPAROS ===
        private void BtnGoToIsoEditor_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.IsoEditor);
        private void BtnGoToPartitions_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Partitions);

        private async Task<int> RunProcessLiveAsync(string exe, string args, string taskId,
            Action<string>? onOutput = null, CancellationToken ct = default)
        {
            using var p = new Process();
            p.StartInfo.FileName = exe;
            p.StartInfo.Arguments = args;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.EnableRaisingEvents = true;

            var lastLine = "";
            var outputLock = new object();
            var progressRegex = new System.Text.RegularExpressions.Regex(@"(\d+)\.\d%");
            int lastProgress = -1;

            DataReceivedEventHandler h = (_, a) =>
            {
                if (a.Data == null) return;
                lock (outputLock) { lastLine = a.Data.Length > 100 ? a.Data[..100] + "..." : a.Data; }
                onOutput?.Invoke(a.Data);

                var m = progressRegex.Match(a.Data);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int pct) && pct != lastProgress)
                {
                    lastProgress = pct;
                    Services.BackgroundTaskTracker.Instance.UpdateTaskProgress(taskId, $"{pct}% — {lastLine}");
                }
            };

            p.OutputDataReceived += h;
            p.ErrorDataReceived += h;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // Timer de progresso — atualiza a cada 5s com tempo decorrido + última linha
            using var progressTimer = new System.Timers.Timer(5000);
            progressTimer.AutoReset = true;
            progressTimer.Elapsed += (_, _) =>
            {
                string line;
                lock (outputLock) { line = lastLine; }
                string elapsed = sw.Elapsed.TotalMinutes >= 1
                    ? $"{sw.Elapsed.Minutes}m{sw.Elapsed.Seconds}s"
                    : $"{sw.Elapsed.Seconds}s";
                string msg = string.IsNullOrEmpty(line)
                    ? $"⏱ {elapsed} — aguardando..."
                    : $"⏱ {elapsed} — {line}";
                Services.BackgroundTaskTracker.Instance.UpdateTaskProgress(taskId, msg);
            };
            progressTimer.Start();

            try
            {
                await p.WaitForExitAsync(ct);
            }
            finally
            {
                progressTimer.Stop();
                sw.Stop();
            }

            return p.ExitCode;
        }

        private async void BtnConsertarViaIso_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Arquivos ISO (*.iso)|*.iso",
                Title = "Selecione a ISO do Windows 11"
            };
            if (dlg.ShowDialog() != true) return;
            string isoPath = dlg.FileName;

            var btn = (Button)sender;
            btn.IsEnabled = false;

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Reparo Windows via ISO", "Dashboard");

            try
            {
                Services.BackgroundTaskTracker.Instance.UpdateTaskProgress(taskId, "Montando ISO...");
                var (mountOk, mountMsg, drive) = await Core.IsoManager.MountIso(isoPath);
                if (!mountOk)
                {
                    mw.ShowInfo("ISO", mountMsg);
                    return;
                }

                string sxsPath = $@"{drive}\sources\sxs";
                if (!Directory.Exists(sxsPath))
                {
                    await Core.IsoManager.DismountIso(isoPath);
                    mw.ShowInfo("ISO", "ISO inválida. Use uma ISO original do Windows 11 com a pasta sources\\sxs.");
                    return;
                }

                mw.ShowInfo("ATENÇÃO", "Reparo em andamento — não feche o KitLugia.");
                Services.BackgroundTaskTracker.Instance.UpdateTaskProgress(taskId, "Etapa 1/2: DISM RestoreHealth...");

                using var cts = new CancellationTokenSource(TimeSpan.FromHours(2));
                var dismOutput = new List<string>();
                int dismExit = await RunProcessLiveAsync("dism",
                    $"/Online /Cleanup-Image /RestoreHealth /Source:\"{sxsPath}\" /LimitAccess",
                    taskId, line => dismOutput.Add(line), cts.Token);
                bool dismOk = dismExit == 0;

                int sfcExit = -1;
                if (dismOk)
                {
                    Services.BackgroundTaskTracker.Instance.UpdateTaskProgress(taskId, "Etapa 2/2: SFC /scannow...");
                    using var sfcCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    sfcExit = await RunProcessLiveAsync("sfc", "/scannow", taskId, ct: sfcCts.Token);
                }

                await Core.IsoManager.DismountIso(isoPath);

                bool sfcOk = sfcExit == 0 || sfcExit == 1;
                string result = dismOk
                    ? (sfcOk ? "DISM + SFC concluídos! Pode ser necessário reiniciar." : "DISM OK. SFC precisa ser executado novamente após reboot.")
                    : "DISM falhou. Verifique se a ISO é compatível.";

                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, dismOk && sfcOk, result);

                if (dismOk && sfcOk)
                    mw.ShowSuccess("Reparo Windows", "DISM e SFC concluídos! Reinicie o PC se solicitado.");
                else if (dismOk)
                    mw.ShowInfo("Reparo Windows", "DISM OK. Reinicie e execute SFC manualmente.");
                else
                    mw.ShowInfo("Reparo Windows", "DISM falhou. Tente outra ISO.");
            }
            catch (OperationCanceledException)
            {
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, "Tempo limite excedido (2h).");
                mw.ShowInfo("Reparo Windows", "Tempo limite excedido. O processo pode ter travado.");
            }
            catch (Exception ex)
            {
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
                mw.ShowInfo("Reparo Windows", $"Erro: {ex.Message}");
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }

        private void BtnGoToWinBoot_Click(object sender, RoutedEventArgs e) =>
            NavigationHelper.NavigateTo(PageType.Winboot);

        private void BtnGoToQuickInstall_Click(object sender, RoutedEventArgs e) =>
            NavigationHelper.NavigateTo(PageType.QuickInstall);
        private void BtnGoToRufus_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Rufus);
        private void BtnGoToRepairs_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Repairs);
        private void BtnGoToDrivers_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Drivers);
        private void BtnGoToScreen_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Screen);
        private void BtnGoToAllTweaks_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.AllTweaks);
        private void BtnGoToExmTweaks_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.ExmTweaks);

        // === ATIVACAO E CONFIGURACOES ===
        private void BtnGoToActivation_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Activation);
        private void BtnGoToTraySettings_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.TraySettings);
        private void BtnGoToUpdate_Click(object sender, RoutedEventArgs e) => NavigationHelper.NavigateTo(PageType.Update);

        // --- AÇÕES DOS BOTÕES GRANDES (1-CLICK) ---

        // --- AÇÕES DOS BOTÕES EXTRAS (1-CLIQUE MODIFICADO) ---

        private void BtnOpenQuickMenu_Click(object sender, RoutedEventArgs e)
        {
            OverlayQuickMenu.Visibility = Visibility.Visible;
            PopulateGpuList();
            UpdateVramRecommendations();
            LoadTraySettingsToQuickMenu();
            PopulateEngineComboBox();
        }

        private void LoadTraySettingsToQuickMenu()
        {
            try
            {
                var mw = Application.Current.MainWindow as MainWindow;
                if (mw?.TrayService != null)
                {
                    ChkTrayIcon.IsChecked = mw.TrayService.IsTrayEnabled;
                    ChkGameBoost.IsChecked = mw.TrayService.GamePriorityEnabled;
                }

                using (var ts = new Microsoft.Win32.TaskScheduler.TaskService())
                {
                    var task = ts.GetTask("KitLugia");
                    ChkStartWithWindows.IsChecked = task?.Enabled == true;
                }
            }
            catch { }
        }


        private void UpdateVramRecommendations()
        {
            try
            {
                double ram = SystemUtils.GetTotalSystemRamGB();
                int recommendedMb = SystemTweaks.GetRecommendedVramMb(ram);

                // Mapeamento: 
                // Item 0: Padrão
                // Item 1: 256
                // Item 2: 512
                // Item 3: 1024
                // Item 4: 2048
                // Item 5: 4096

                foreach (ComboBoxItem item in CmbVram.Items)
                {
                    string content = item.Content.ToString() ?? "";
                    // Limpa recomendações anteriores se houver
                    content = content.Replace(" (Recomendado)", "");

                    bool isRecommended = false;
                    if (content.Contains("256 MB") && recommendedMb == 256) isRecommended = true;
                    else if (content.Contains("512 MB") && recommendedMb == 512) isRecommended = true;
                    else if (content.Contains("1024 MB") && recommendedMb == 1024) isRecommended = true;
                    else if (content.Contains("2048 MB") && recommendedMb == 2048) isRecommended = true;

                    if (isRecommended)
                    {
                        item.Content = content + " (Recomendado)";
                        item.IsSelected = true; // Auto-seleciona o recomendado
                    }
                    else
                    {
                        item.Content = content;
                    }
                }
            }
            catch { }
        }

        private void BtnCancelQuickMenu_Click(object sender, RoutedEventArgs e)
        {
            OverlayQuickMenu.Visibility = Visibility.Collapsed;
        }

        private void PopulateGpuList()
        {
            try
            {
                CmbGpu.Items.Clear();
                var gpuNames = SystemTweaks.GetAllGpuNames();
                
                foreach (var name in gpuNames)
                {
                    CmbGpu.Items.Add(name);
                }

                if (CmbGpu.Items.Count > 0) CmbGpu.SelectedIndex = 0;
            }
            catch { }
        }

        private async void BtnApplyCustomOptimization_Click(object sender, RoutedEventArgs e)
        {
            OverlayQuickMenu.Visibility = Visibility.Collapsed;

            var settings = new KitLugia.Core.OptimizationSettings
            {
                ApplyRegistryTweaks = ChkSystemTweaks.IsChecked == true,
                ApplyPowerPlan = ChkPowerPlan.IsChecked == true,
                ApplyGamingOptimizations = true, // Sempre ativado no 1-clique
                ApplyVerboseBoot = true,
                ApplyVramTweak = true,
                UseExtremeProfile = ChkExtremeVisuals.IsChecked == true
            };

            if (ChkTurboShutdown.IsChecked == true)
            {
                SystemTweaks.ToggleFastShutdown();
            }

            ApplyTraySettingsFromQuickMenu();

            // Configura motor do GameBoost conforme seleção do usuário
            // Se um motor personalizado já está ativo, não sobrescreve
            if (!Services.TrayIconService.IsCustomEngineActive)
            {
                if (CmbGameEngine.SelectedItem is ComboBoxItem selectedEngine && selectedEngine.Tag != null)
                {
                    var tagValue = selectedEngine.Tag.ToString();
                    if (int.TryParse(tagValue, out int engineNumber))
                    {
                        Services.TrayIconService.SetEngine(engineNumber);
                        KitLugia.Core.Logger.Log($"🎮 Dashboard: GameBoost configurado para motor V{engineNumber} via Otimização Inteligente");
                    }
                    else
                    {
                        var customProfile = _customProfiles.FirstOrDefault(p => p.Id == tagValue);
                        if (customProfile != null)
                        {
                            var config = new Services.CustomEngineConfig
                            {
                                CpuPriority = customProfile.CpuPriority,
                                IoPriorityLevel = customProfile.IoPriority,
                                PagePriorityLevel = customProfile.PagePriority,
                                TimerBoost = customProfile.TimerResolution,
                                EcoQoSEnabled = customProfile.EcoQoS,
                                ProBalance = customProfile.ProBalanceEnabled,
                                ProBalanceCpuThreshold = customProfile.ProBalanceThreshold,
                                NetworkBoost = customProfile.NetworkBoost,
                                ThreadMemoryPriority = customProfile.ThreadMemoryPriority,
                                ThreadEfficiencyMode = customProfile.ThreadEfficiencyMode,
                                GameClassInfo = customProfile.GameClassInfo,
                                Win32PrioritySeparation = customProfile.Win32PrioritySeparation
                            };
                            Services.TrayIconService.SetCustomEngine(config);
                            KitLugia.Core.Logger.Log($"🎮 Dashboard: GameBoost configurado para perfil customizado {customProfile.Name}");
                        }
                    }
                }
            }

            // Se um motor personalizado estava ativo, confirma que permanece
            if (Services.TrayIconService.IsCustomEngineActive)
            {
                KitLugia.Core.Logger.Log($"🎮 Dashboard: Motor personalizado mantido ativo via Otimização Inteligente");
            }

            // Detectar RegPath da GPU selecionada - Usa método seguro sem ManagementObject
            int index = CmbGpu.SelectedIndex;
            var gpuNames = SystemTweaks.GetAllGpuNames();
            if (index >= 0 && index < gpuNames.Count)
            {
                settings.TargetGpuRegPath = SystemTweaks.FindGpuRegistryPathByDescription(gpuNames[index])!;
            }

            // Mapear VRAM
            int vramIndex = CmbVram.SelectedIndex;
            settings.VramSizeMb = vramIndex switch
            {
                1 => 256,
                2 => 512,
                3 => 1024,
                4 => 2048,
                5 => 4096,
                _ => 0 // Automatic
            };

            await RunOptimizationFlow(settings);
        }

        private void PopulateEngineComboBox()
        {
            CmbGameEngine.Items.Clear();
            _customProfiles.Clear();

            var v1 = new ComboBoxItem { Content = "🟢 V1 - Original Plus (Win32Priority)", Tag = "1", Foreground = System.Windows.Media.Brushes.White };
            var v2 = new ComboBoxItem { Content = "🟡 V2 - FPS Estável Plus (P-Cores+GameMode)", Tag = "2", Foreground = System.Windows.Media.Brushes.White };
            var v3 = new ComboBoxItem { Content = "🔴 V3 - Extremo Plus (Tudo no máximo)", Tag = "3", Foreground = System.Windows.Media.Brushes.White };
            var v4 = new ComboBoxItem { Content = "💥 V4 - Extreme Pro (RealTime)", Tag = "4", Foreground = System.Windows.Media.Brushes.Orange };
            CmbGameEngine.Items.Add(v1);
            CmbGameEngine.Items.Add(v2);
            CmbGameEngine.Items.Add(v3);
            CmbGameEngine.Items.Add(v4);

            try
            {
                if (File.Exists(_profilesFilePath))
                {
                    var json = File.ReadAllText(_profilesFilePath);
                    var profiles = JsonSerializer.Deserialize<List<CustomMotorProfile>>(json);
                    if (profiles != null && profiles.Count > 0)
                    {
                        _customProfiles = profiles;
                        foreach (var profile in _customProfiles)
                        {
                            var item = new ComboBoxItem
                            {
                                Content = $"⚙️ {profile.Name}",
                                Tag = profile.Id,
                                Foreground = System.Windows.Media.Brushes.White
                            };
                            CmbGameEngine.Items.Add(item);
                        }
                    }
                }
            }
            catch { }

            var customOption = new ComboBoxItem { Content = "⚙️ Personalizar...", Tag = "custom", Foreground = System.Windows.Media.Brushes.Gold };
            CmbGameEngine.Items.Add(customOption);

            // Restaura a seleção do motor atual
            if (CmbGameEngine.Items.Count > 0)
            {
                string? currentTag = null;
                if (Services.TrayIconService.IsCustomEngineActive)
                {
                    // Procura perfil customizado ativo
                    foreach (var p in _customProfiles)
                    {
                        currentTag = p.Id;
                        break;
                    }
                }
                else
                {
                    currentTag = ((int)Services.TrayIconService.CurrentEngine).ToString();
                }

                if (currentTag != null)
                {
                    for (int i = 0; i < CmbGameEngine.Items.Count; i++)
                    {
                        if (CmbGameEngine.Items[i] is ComboBoxItem item && item.Tag?.ToString() == currentTag)
                        {
                            CmbGameEngine.SelectedIndex = i;
                            return;
                        }
                    }
                }
                CmbGameEngine.SelectedIndex = 0;
            }
        }

        private void CmbGameEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbGameEngine.SelectedItem is ComboBoxItem selected && selected.Tag?.ToString() == "custom")
            {
                CmbGameEngine.SelectedIndex = 0;
                var mw = Application.Current.MainWindow as MainWindow;
                if (mw != null)
                {
                    OverlayQuickMenu.Visibility = Visibility.Collapsed;
                    Pages.GameBoostPage.AutoOpenCustomOverlayOnLoad = true;
                    mw.NavigateToPage(PageType.GameBoost);
                }
            }
        }

        private async void BtnRevertNormal_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;

            bool confirm = await mw.ShowConfirmationDialog(
                "Isso removerá todas as otimizações de VRAM, planos de energia e modificações de registro do Kit Lugia.\n\n" +
                "Deseja restaurar o padrão do sistema?");

            if (!confirm) return;

            mw.ShowInfo("REVERTENDO", "Restaurando configurações padrão do sistema...");
            
            var progress = new Progress<string>(s => { });
            try
            {
                await OptimizationOrchestrator.RevertAllOptimizationsAsync(progress);
                mw.ShowSuccess("SUCESSO", "Sistema restaurado! Reinicie para concluir a reversão.");
            }
            catch (Exception ex)
            {
                mw.ShowError("ERRO", $"Falha ao reverter: {ex.Message}");
            }
        }

        private void ApplyTraySettingsFromQuickMenu()
        {
            try
            {
                var mw = Application.Current.MainWindow as MainWindow;
                if (mw?.TrayService != null)
                {
                    mw.TrayService.SetTrayEnabled(ChkTrayIcon.IsChecked == true);
                    mw.TrayService.GamePriorityEnabled = ChkGameBoost.IsChecked == true;
                    mw.TrayService.SaveSettings();

                    KitLugia.GUI.Services.TrayIconService.SetAutoStart(ChkStartWithWindows.IsChecked == true);
                    

                    KitLugia.Core.Logger.Log($"🎮 Dashboard: GameBoost {(ChkGameBoost.IsChecked == true ? "ativado" : "desativado")}");
                }
            }
            catch { }
        }

        // Lógica compartilhada de execução
        private async Task RunOptimizationFlow(KitLugia.Core.OptimizationSettings settings)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;

            string taskId = Guid.NewGuid().ToString();
            BackgroundTaskTracker.Instance.RegisterTask(taskId, "Otimização", "Dashboard");

            mw.ShowInfo("PROCESSANDO", "Aplicando otimizações selecionadas...");

            var progress = new Progress<string>(s => { });

            try
            {
                await OptimizationOrchestrator.RunOptimizationAsync(settings, progress);
                BackgroundTaskTracker.Instance.CompleteTask(taskId, true);
                mw.ShowSuccess("SUCESSO", "Otimização concluída com sucesso! Reinicie o computador.");
            }
            catch (Exception ex)
            {
                BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
                mw.ShowError("ERRO", $"Falha na otimização: {ex.Message}");
            }
        }

        // Métodos antigos removidos para evitar duplicidade ou confusão
        [Obsolete("Use BtnOpenQuickMenu_Click")]
        private void BtnOptimizeStandard_Click(object sender, RoutedEventArgs e) => BtnOpenQuickMenu_Click(sender, e);

        [Obsolete("Use Selection Overlay instead")]
        private void BtnOptimizeExtreme_Click(object sender, RoutedEventArgs e) => BtnOpenQuickMenu_Click(sender, e);

        // --- GAMING LATENCY PROFILE EVENT HANDLERS ---

        private void BtnOpenLatencyMenu_Click(object sender, RoutedEventArgs e)
        {
            OverlayLatencyMenu.Visibility = Visibility.Visible;
            RefreshLatencyStatus();
        }

        private void BtnCancelLatencyMenu_Click(object sender, RoutedEventArgs e)
        {
            OverlayLatencyMenu.Visibility = Visibility.Collapsed;
        }

        private void BtnRevertLatency_Click(object sender, RoutedEventArgs e)
        {
            var result = SystemTweaks.RevertGamingLatencyProfile();
            MessageBox.Show(result.Message, result.Success ? "Sucesso" : "Erro", 
                MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }

        private void BtnRevertLatencyMenu_Click(object sender, RoutedEventArgs e)
        {
            var result = SystemTweaks.RevertGamingLatencyProfile();
            if (result.Success)
            {
                MessageBox.Show(result.Message, "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshLatencyStatus();
            }
            else
            {
                MessageBox.Show(result.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnApplyAllLatency_Click(object sender, RoutedEventArgs e)
        {
            int win32Value = 0x26;
            switch (CmbWin32Priority.SelectedIndex)
            {
                case 0: win32Value = 0x18; break;
                case 1: win32Value = 0x26; break;
                case 2: win32Value = 0x2A; break;
            }

            var result = SystemTweaks.ApplyFullGamingLatencyProfile(win32Value);
            
            if (result.Success)
            {
                string tweaksAplicados = string.Join("\n• ", result.Applied);
                MessageBox.Show($"{result.Message}\n\nTweaks aplicados:\n• {tweaksAplicados}", 
                    "Gaming Latency Profile", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshLatencyStatus();
            }
            else
            {
                MessageBox.Show(result.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- TOGGLE METHODS COM CORES VISUAIS ---

        private void BtnToggleCoreParking_Click(object sender, RoutedEventArgs e)
        {
            var status = SystemTweaks.CheckGamingLatencyStatus();
            bool isCurrentlyOn = status["CoreParking"];
            
            if (!isCurrentlyOn)
            {
                var result = SystemTweaks.DisableCoreParking();
                if (result.Success) UpdateToggleVisual(BtnToggleCoreParking, IndicatorCoreParking, BorderCoreParking, true);
                MessageBox.Show(result.Message, "Core Parking", MessageBoxButton.OK, 
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            else
            {
                // Revert individual - restaura valor padrão (64)
                try
                {
                    Microsoft.Win32.Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c8-3b32988b1dd4\0cc5b647-c1df-4637-891a-dec35c318583", "ValueMax", 64, Microsoft.Win32.RegistryValueKind.DWord);
                    UpdateToggleVisual(BtnToggleCoreParking, IndicatorCoreParking, BorderCoreParking, false);
                    MessageBox.Show("Core Parking restaurado para padrão.", "Core Parking", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao restaurar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            RefreshLatencyStatus();
        }

        private void BtnToggleTimerCoalescing_Click(object sender, RoutedEventArgs e)
        {
            var status = SystemTweaks.CheckGamingLatencyStatus();
            bool isCurrentlyOn = status["TimerCoalescing"];
            
            if (!isCurrentlyOn)
            {
                var result = SystemTweaks.DisableTimerCoalescing();
                if (result.Success) UpdateToggleVisual(BtnToggleTimerCoalescing, IndicatorTimerCoalescing, BorderTimerCoalescing, true);
                MessageBox.Show(result.Message, "Timer Coalescing", MessageBoxButton.OK, 
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            else
            {
                // Revert individual - deleta a chave para voltar ao padrão
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", true);
                    if (key != null)
                    {
                        key.DeleteValue("CoalescingTimerInterval", false);
                    }
                    UpdateToggleVisual(BtnToggleTimerCoalescing, IndicatorTimerCoalescing, BorderTimerCoalescing, false);
                    MessageBox.Show("Timer Coalescing restaurado para padrão.", "Timer Coalescing", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao restaurar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            RefreshLatencyStatus();
        }

        private void BtnToggleInputQueue_Click(object sender, RoutedEventArgs e)
        {
            var status = SystemTweaks.CheckGamingLatencyStatus();
            bool isCurrentlyOn = status["InputQueue"];
            
            if (!isCurrentlyOn)
            {
                var result = SystemTweaks.OptimizeInputQueue();
                if (result.Success) UpdateToggleVisual(BtnToggleInputQueue, IndicatorInputQueue, BorderInputQueue, true);
                MessageBox.Show(result.Message, "Input Queue", MessageBoxButton.OK, 
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            else
            {
                // Revert individual - restaura para 100 (padrão)
                try
                {
                    Microsoft.Win32.Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", 100, Microsoft.Win32.RegistryValueKind.DWord);
                    Microsoft.Win32.Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", 100, Microsoft.Win32.RegistryValueKind.DWord);
                    UpdateToggleVisual(BtnToggleInputQueue, IndicatorInputQueue, BorderInputQueue, false);
                    MessageBox.Show("Input Queue restaurado para padrão (100).", "Input Queue", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao restaurar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            RefreshLatencyStatus();
        }

        private void BtnToggleGlobalTimer_Click(object sender, RoutedEventArgs e)
        {
            var status = SystemTweaks.CheckGamingLatencyStatus();
            bool isCurrentlyOn = status["GlobalTimerResolution"];
            
            if (!isCurrentlyOn)
            {
                var result = SystemTweaks.EnableGlobalTimerResolution();
                if (result.Success) UpdateToggleVisual(BtnToggleGlobalTimer, IndicatorGlobalTimer, BorderGlobalTimer, true);
                MessageBox.Show(result.Message, "Global Timer Resolution", MessageBoxButton.OK, 
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            else
            {
                // Revert individual
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", true);
                    key?.DeleteValue("GlobalTimerResolutionRequests", false);
                    UpdateToggleVisual(BtnToggleGlobalTimer, IndicatorGlobalTimer, BorderGlobalTimer, false);
                    MessageBox.Show("Global Timer Resolution restaurado para padrão.", "Global Timer", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao restaurar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            RefreshLatencyStatus();
        }

        private void BtnToggleSysResp_Click(object sender, RoutedEventArgs e)
        {
            var status = SystemTweaks.CheckGamingLatencyStatus();
            bool isCurrentlyOn = status["SystemResponsiveness"];
            
            if (!isCurrentlyOn)
            {
                var result = SystemTweaks.SetSystemResponsivenessGaming();
                if (result.Success) UpdateToggleVisual(BtnToggleSysResp, IndicatorSysResp, BorderSysResp, true);
                MessageBox.Show(result.Message, "System Responsiveness", MessageBoxButton.OK, 
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            else
            {
                // Revert individual - restaura para 20 (padrão)
                try
                {
                    Microsoft.Win32.Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 20, Microsoft.Win32.RegistryValueKind.DWord);
                    UpdateToggleVisual(BtnToggleSysResp, IndicatorSysResp, BorderSysResp, false);
                    MessageBox.Show("System Responsiveness restaurado para padrão (20).", "System Responsiveness", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Erro ao restaurar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            RefreshLatencyStatus();
        }

        private void UpdateToggleVisual(Button btn, System.Windows.Shapes.Ellipse indicator, Border container, bool isOn)
        {
            if (isOn)
            {
                btn.Content = "ON";
                btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // #4CAF50
                btn.Foreground = System.Windows.Media.Brushes.White;
                btn.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
                indicator.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Verde
                container.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
                container.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 55, 37)); // Verde escuro
            }
            else
            {
                btn.Content = "OFF";
                btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51)); // #333
                btn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(153, 153, 153)); // #999
                btn.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(85, 85, 85)); // #555
                indicator.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 102, 102)); // #666
                container.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51)); // #333
                container.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 37)); // #252525
            }
        }

        #region Latency Analyzer Event Handlers

        private CancellationTokenSource? _latencyScanCts;

        private async void BtnScanLatency_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Mostra loading overlay
                LoadingOverlayBenchmark.Visibility = Visibility.Visible;
                ProgressBarBenchmark.Value = 0;
                TxtBenchmarkStatus.Text = "Iniciando benchmark...";
                TxtBenchmarkLog.Text = "";
                BtnCancelBenchmark.IsEnabled = true;

                _latencyScanCts = new CancellationTokenSource();
                
                // Progress reporter para atualizar a UI
                var progress = new Progress<string>(msg =>
                {
                    TxtBenchmarkStatus.Text = msg;
                    TxtBenchmarkLog.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
                    ScrollViewerBenchmarkLog.ScrollToBottom();
                    
                    // Atualiza progresso baseado na mensagem
                    if (msg.Contains("PADRÃO")) ProgressBarBenchmark.Value = 25;
                    else if (msg.Contains("CONSERVADOR")) ProgressBarBenchmark.Value = 50;
                    else if (msg.Contains("EQUILIBRADO")) ProgressBarBenchmark.Value = 75;
                    else if (msg.Contains("AGRESSIVO")) ProgressBarBenchmark.Value = 90;
                    else if (msg.Contains("Aplicando")) ProgressBarBenchmark.Value = 100;
                });

                // Executa benchmark inteligente completo
                var benchmark = await LatencyAnalyzer.RunIntelligentBenchmarkAsync(progress, _latencyScanCts.Token);

                // Esconde loading
                LoadingOverlayBenchmark.Visibility = Visibility.Collapsed;

                if (benchmark.Success)
                {
                    // Mostra resultados do melhor perfil
                    TxtCurrentLatency.Text = benchmark.Best.Measurement.CurrentLatencyUs.ToString("F1");
                    TxtAvgLatency.Text = benchmark.Best.Measurement.AvgLatencyUs.ToString("F1");
                    TxtMaxLatency.Text = benchmark.Best.Measurement.MaxLatencyUs.ToString("F1");
                    
                    // Mostra relatório completo
                    TxtLatencyRecommendation.Text = $"🏆 Melhor: {benchmark.Best.Profile.Name}\n" +
                        $"Latência: {benchmark.Best.Measurement.AvgLatencyUs:F1}µs | Score: {benchmark.Best.Score:F0}\n" +
                        $"Estável: {(benchmark.Best.IsStable ? "Sim" : "Não")}\n\n" +
                        benchmark.Report;
                    
                    PanelLatencyResults.Visibility = Visibility.Visible;
                    
                    // Atualiza toggles para refletir o perfil aplicado
                    RefreshLatencyStatus();
                    
                    Logger.Log($"Benchmark completo. Melhor perfil: {benchmark.Best.Profile.Name}");
                    
                    // Mostra mensagem de sucesso
                    MessageBox.Show($"Benchmark concluído!\n\nMelhor perfil: {benchmark.Best.Profile.Name}\n" +
                        $"Latência: {benchmark.Best.Measurement.AvgLatencyUs:F1}µs\n" +
                        $"As configurações ótimas já foram aplicadas automaticamente.", 
                        "Benchmark Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    TxtLatencyRecommendation.Text = $"Erro no benchmark: {benchmark.Report}";
                    PanelLatencyResults.Visibility = Visibility.Visible;
                    MessageBox.Show($"Erro no benchmark: {benchmark.Report}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (OperationCanceledException)
            {
                LoadingOverlayBenchmark.Visibility = Visibility.Collapsed;
                TxtLatencyRecommendation.Text = "Benchmark cancelado pelo usuário.";
                PanelLatencyResults.Visibility = Visibility.Visible;
                Logger.Log("Benchmark cancelado pelo usuário");
            }
            catch (Exception ex)
            {
                LoadingOverlayBenchmark.Visibility = Visibility.Collapsed;
                MessageBox.Show($"Erro no benchmark: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnScanLatency.IsEnabled = true;
                BtnScanLatency.Content = "ANALISAR";
            }
        }

        private void BtnCancelBenchmark_Click(object sender, RoutedEventArgs e)
        {
            _latencyScanCts?.Cancel();
            BtnCancelBenchmark.IsEnabled = false;
            TxtBenchmarkStatus.Text = "Cancelando...";
            Logger.Log("Solicitação de cancelamento do benchmark");
        }

        private Dictionary<string, string> _currentRecommendations = new();

        private async void BtnApplyRecommended_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnApplyRecommended.IsEnabled = false;
                BtnApplyRecommended.Content = "APLICANDO...";

                var result = await LatencyAnalyzer.AutoOptimizeAsync();
                
                if (result.Success)
                {
                    MessageBox.Show(result.Message, "Otimização Concluída", MessageBoxButton.OK, MessageBoxImage.Information);
                    RefreshLatencyStatus();
                    
                    // Atualiza os valores na UI
                    TxtCurrentLatency.Text = result.After.CurrentLatencyUs.ToString("F1");
                    TxtAvgLatency.Text = result.After.AvgLatencyUs.ToString("F1");
                    TxtMaxLatency.Text = result.After.MaxLatencyUs.ToString("F1");
                }
                else
                {
                    MessageBox.Show(result.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao aplicar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnApplyRecommended.IsEnabled = true;
                BtnApplyRecommended.Content = "APLICAR RECOMENDAÇÕES";
            }
        }

        private async Task AnimateProgressBarAsync(CancellationToken cancellationToken)
        {
            try
            {
                double progress = 0;
                while (progress < 100 && !cancellationToken.IsCancellationRequested)
                {
                    progress += 2;
                    ProgressLatencyScan.Value = progress;
                    await Task.Delay(100, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal quando cancela
            }
        }

        #endregion

        // ============================================================
        // HANDLERS: OTIMIZAÇÃO DO CRIADOR (COLUNA DIREITA)
        // ============================================================

        private void BtnAtivarTodosCriador_Click(object sender, RoutedEventArgs e)
        {
            // ATIVADOS: check = ativar
            if (ChkCrBgApps.IsChecked != true) ChkCrBgApps.IsChecked = true;
            if (ChkCrDiag.IsChecked != true) ChkCrDiag.IsChecked = true;
            if (ChkCrGameBar.IsChecked != true) ChkCrGameBar.IsChecked = true;
            if (ChkCrGdi.IsChecked != true) ChkCrGdi.IsChecked = true;
            if (ChkCrNdu.IsChecked != true) ChkCrNdu.IsChecked = true;
            if (ChkCrPcie.IsChecked != true) ChkCrPcie.IsChecked = true;
            if (ChkCrBing.IsChecked != true) ChkCrBing.IsChecked = true;
            if (ChkCrPowerThrottle.IsChecked != true) ChkCrPowerThrottle.IsChecked = true;
            if (ChkCrVbs.IsChecked != true) ChkCrVbs.IsChecked = true;
            if (ChkCrDiskTimeout.IsChecked != true) ChkCrDiskTimeout.IsChecked = true;
            if (ChkCrTurboShutdown.IsChecked != true) ChkCrTurboShutdown.IsChecked = true;
            if (ChkCrGameBoost.IsChecked != true) ChkCrGameBoost.IsChecked = true;
            if (ChkCrTray.IsChecked != true) ChkCrTray.IsChecked = true;
            if (ChkCrNoReboot.IsChecked != true) ChkCrNoReboot.IsChecked = true;
            if (ChkCrAutoStart.IsChecked != true) ChkCrAutoStart.IsChecked = true;
            if (ChkCrExtremeLatency.IsChecked != true) ChkCrExtremeLatency.IsChecked = true;
            if (ChkCrFsutil.IsChecked != true) ChkCrFsutil.IsChecked = true;
            if (ChkCrGameMode.IsChecked != true) ChkCrGameMode.IsChecked = true;
            if (ChkCrBgRun.IsChecked != true) ChkCrBgRun.IsChecked = true;
            if (ChkCrUnpark.IsChecked != true) ChkCrUnpark.IsChecked = true;
        }

        private void ChkCrBgApps_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrBgApps.IsChecked == true)
                SystemTweaks.DisableBackgroundApps();
            else
                SystemTweaks.EnableBackgroundApps();
        }

        private void ChkCrDiag_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrDiag.IsChecked == true)
                SystemTweaks.DisableDiagTrack();
            else
                SystemTweaks.EnableDiagTrack();
        }

        private void ChkCrGameBar_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrGameBar.IsChecked == true)
                SystemTweaks.DisableGameBar();
            else
                SystemTweaks.EnableGameBar();
        }

        private void ChkCrGdi_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrGdi.IsChecked == true)
                SystemTweaks.DisableGdiScaling();
        }

        private void ChkCrNdu_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrNdu.IsChecked == true)
                SystemTweaks.DisableNDU();
            else
                SystemTweaks.EnableNDU();
        }

        private void ChkCrPcie_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrPcie.IsChecked == true)
                SystemTweaks.DisablePcieLinkStatePowerManagement();
        }

        private void ChkCrBing_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrBing.IsChecked == true)
                SystemTweaks.ApplyBingTweak();
            else
                SystemTweaks.EnableWebSearch();
        }

        private void ChkCrPowerThrottle_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrPowerThrottle.IsChecked == true)
                SystemTweaks.DisablePowerThrottling();
            else
                SystemTweaks.EnablePowerThrottling();
        }

        private void ChkCrVbs_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrVbs.IsChecked == true)
                SystemTweaks.DisableVBSCodeIntegrity();
        }

        private void ChkCrDiskTimeout_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrDiskTimeout.IsChecked == true)
                SystemTweaks.DisableHardDiskDisplayTimeout();
            else
                SystemTweaks.EnableHardDiskDisplayTimeout();
        }

        private void ChkCrTurboShutdown_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw?.TrayService != null)
            {
                mw.TrayService.TurboShutdownEnabled = ChkCrTurboShutdown.IsChecked == true;
                mw.TrayService.SaveSettings();
            }
        }

        private void ChkCrGameBoost_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw?.TrayService != null)
            {
                mw.TrayService.GamePriorityEnabled = ChkCrGameBoost.IsChecked == true;
                mw.TrayService.SaveSettings();
            }
        }

        private void ChkCrTray_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw?.TrayService != null)
            {
                mw.TrayService.IsTrayEnabled = ChkCrTray.IsChecked == true;
                mw.TrayService.SaveSettings();
            }
        }

        private void ChkCrNoReboot_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrNoReboot.IsChecked == true)
                SystemTweaks.EnableNoAutoReboot();
            else
                SystemTweaks.DisableNoAutoReboot();
        }

        private void ChkCrAutoStart_Click(object sender, RoutedEventArgs e)
        {
            Services.TrayIconService.SetAutoStart(ChkCrAutoStart.IsChecked == true);
        }

        private void ChkCrExtremeLatency_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrExtremeLatency.IsChecked == true)
                BtnApplyAllLatency_Click(sender, e);
            else
                SystemTweaks.RevertGamingLatencyProfile();
        }

        private void ChkCrFsutil_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrFsutil.IsChecked == true)
                SystemTweaks.ToggleMemoryUsage();
        }

        private void ChkCrGameMode_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrGameMode.IsChecked == true)
                SystemTweaks.ApplyGamingOptimizations();
            else
                SystemTweaks.RevertGamingOptimizations();
        }

        private void ChkCrBgRun_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw?.TrayService != null)
            {
                mw.TrayService.CloseToTray = ChkCrBgRun.IsChecked == true;
                mw.TrayService.SaveSettings();
            }
        }

        private void ChkCrUnpark_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCrUnpark.IsChecked == true)
                SystemTweaks.UnparkCpuPowerConfig();
        }

        private void RefreshLatencyStatus()
        {
            try
            {
                var status = SystemTweaks.CheckGamingLatencyStatus();

                // Típico: 10-20 linhas de status de latência
                var sb = new System.Text.StringBuilder(512);
                
                // Atualiza visuais dos toggles
                UpdateToggleVisual(BtnToggleCoreParking, IndicatorCoreParking, BorderCoreParking, status["CoreParking"]);
                UpdateToggleVisual(BtnToggleTimerCoalescing, IndicatorTimerCoalescing, BorderTimerCoalescing, status["TimerCoalescing"]);
                UpdateToggleVisual(BtnToggleInputQueue, IndicatorInputQueue, BorderInputQueue, status["InputQueue"]);
                UpdateToggleVisual(BtnToggleGlobalTimer, IndicatorGlobalTimer, BorderGlobalTimer, status["GlobalTimerResolution"]);
                UpdateToggleVisual(BtnToggleSysResp, IndicatorSysResp, BorderSysResp, status["SystemResponsiveness"]);
                
                foreach (var item in status)
                {
                    string statusText = item.Value ? "✓ Ativado" : "✗ Padrão";
                    sb.AppendLine($"{item.Key}: {statusText}");
                }

                TxtLatencyStatus.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                TxtLatencyStatus.Text = "Erro ao verificar status: " + ex.Message;
            }
        }
    }
}
