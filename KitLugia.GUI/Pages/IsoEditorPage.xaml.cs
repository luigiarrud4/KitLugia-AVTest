using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        private List<WimEdition> _editions = new();
        private string _workRoot = Path.Combine(Path.GetTempPath(), "KitLugia_IsoEditor_Work");

        public IsoEditorPage()
        {
            InitializeComponent();
            this.Unloaded += IsoEditorPage_Unloaded;
            ComboCompression.SelectedIndex = 0;
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
                BtnAnalyzeIso.IsEnabled = true;
                TxtDetectedIsoType.Text = $"ISO selecionada: {Path.GetFileName(_isoPath)}";
            }
        }

        private void TxtIsoPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(TxtIsoPath.Text) && File.Exists(TxtIsoPath.Text))
            {
                _isoPath = TxtIsoPath.Text;
                BtnAnalyzeIso.IsEnabled = true;
                TxtDetectedIsoType.Text = $"✅. ISO selecionada: {Path.GetFileName(_isoPath)}";
            }
        }

        /// <summary>
        /// ANÁLISE RÁPIDA: extrai só o sources\install.wim/esd da ISO (uma stream) e
        /// lista as edições via wimlib info - sem montar nada. Resultado vai para o ComboEditions.
        /// </summary>
        private async void BtnAnalyzeIso_Click(object sender, RoutedEventArgs e)
        {
            if (_isIsoEditorOperation) return;
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;
            if (string.IsNullOrEmpty(_isoPath) || !File.Exists(_isoPath))
            {
                mw.ShowError("ERRO", "Selecione uma imagem ISO primeiro.");
                return;
            }

            _isIsoEditorOperation = true;
            ShowBusy("💿 KIT ISO EDITOR - ANALISANDO");
            SetBusyStatus("Analisando a ISO (drive montado, sem extrair)...", 3, "Analisando a ISO");
            try
            {
                AddLog("Análise rápida - montando ISO e lendo sources\\install.* direto do drive (estilo Titus, sem extrair)...");
                var (ok, msg, editions, installPath) = await AnalyzeIsoMountedAsync();
                if (!ok || editions.Count == 0 || string.IsNullOrEmpty(installPath))
                {
                    // Fallback: extração 7z do install.* (máquina sem mount de ISO / mount falhou)
                    SetBusyStatus("Fallback: extraindo install.wim/esd da ISO...", 4, "Extraindo install da ISO");
                    AddLog("Fallback: extraindo apenas sources\\install.* da ISO com 7z...");
                    string installFile = await ExtractInstallFileOnlyAsync();
                    if (string.IsNullOrEmpty(installFile) || !File.Exists(installFile))
                    {
                        mw.ShowError("ERRO", "Não foi encontrado sources\\install.wim ou install.esd na ISO.");
                        return;
                    }

                    SetBusyStatus("Listando edições com wimlib...", 6, "Listando edições");
                    AddLog($"Analisando {Path.GetFileName(installFile)} com wimlib...");
                    (ok, msg, editions) = await IsoEditorManager.AnalyzeWimAsync(installFile);
                    if (!ok)
                    {
                        mw.ShowError("ERRO", msg);
                        return;
                    }
                }

                _editions = editions;
                ComboEditions.ItemsSource = _editions;
                ComboEditions.SelectedIndex = 0;
                TxtAnalysisResult.Text = msg + "\n" + string.Join("\n", editions.Select(x => $"  {x.Index}. {x.Name}"));
                AddLog(msg);
                AddLog($"Edições: {string.Join(" | ", editions.Select(x => $"{x.Index}: {x.Name}"))}");
                TxtStatus.Text = "✅. Análise concluída.";
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnAnalyzeIso_Click", ex.Message);
                mw.ShowError("ERRO", $"Falha na análise: {ex.Message}");
            }
            finally
            {
                _isIsoEditorOperation = false;
                OverlayBusy.Visibility = Visibility.Collapsed;
            }
        }

        private string GetIsoWorkFolder()
        {
            string name = Path.GetFileNameWithoutExtension(_isoPath) ?? "iso";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(_workRoot, name);
        }

        /// <summary>
        /// Estilo Chris Titus: monta a ISO e analisa o sources\install.wim/esd DIRETO do drive
        /// virtual (sem extrair nada). Fallback 7z fica nos chamadores. Retorna o path usado.
        /// </summary>
        private async Task<(bool Ok, string Msg, List<WimEdition> Editions, string InstallPath)> AnalyzeIsoMountedAsync()
        {
            try
            {
                var (mOk, _, drive) = await Core.IsoManager.MountIso(_isoPath);
                if (mOk && !string.IsNullOrWhiteSpace(drive))
                {
                    try
                    {
                        string srcRoot = $"{drive.TrimEnd('\\', ':')}:\\";
                        string srcWim = Path.Combine(srcRoot, "sources", "install.wim");
                        string srcEsd = Path.Combine(srcRoot, "sources", "install.esd");
                        string src = File.Exists(srcWim) ? srcWim : File.Exists(srcEsd) ? srcEsd : "";
                        if (!string.IsNullOrEmpty(src))
                        {
                            AddLog($"Lendo {src} direto do drive montado...");
                            var (ok, msg, editions) = await IsoEditorManager.AnalyzeWimAsync(src);
                            return (ok, msg, editions, src);
                        }
                    }
                    finally
                    {
                        await Core.IsoManager.DismountIso(_isoPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("AnalyzeIsoMountedAsync", ex.Message);
            }
            return (false, "", new List<WimEdition>(), "");
        }

        /// <summary>
        /// Extrai SO o install.wim/esd da ISO (rápido) para pasta de trabalho persistente.
        /// Estilo Titus: monta a ISO e copia nativamente; 7z só como fallback.
        /// </summary>
        private async Task<string> ExtractInstallFileOnlyAsync()
        {
            string workDir = GetIsoWorkFolder();
            Directory.CreateDirectory(workDir);

            string existingWim = Path.Combine(workDir, "install.wim");
            string existingEsd = Path.Combine(workDir, "install.esd");
            if (File.Exists(existingWim)) return existingWim;
            if (File.Exists(existingEsd)) return existingEsd;

            // Monta a ISO e copia o install.* direto do drive (cópia nativa, mais rápido que 7z)
            try
            {
                var (mOk, _, drive) = await Core.IsoManager.MountIso(_isoPath);
                if (mOk && !string.IsNullOrWhiteSpace(drive))
                {
                    try
                    {
                        string srcRoot = $"{drive.TrimEnd('\\', ':')}:\\";
                        string srcWim = Path.Combine(srcRoot, "sources", "install.wim");
                        string srcEsd = Path.Combine(srcRoot, "sources", "install.esd");
                        if (File.Exists(srcWim))
                        {
                            File.Copy(srcWim, existingWim, true);
                            return existingWim;
                        }
                        if (File.Exists(srcEsd))
                        {
                            File.Copy(srcEsd, existingEsd, true);
                            return existingEsd;
                        }
                    }
                    finally
                    {
                        await Core.IsoManager.DismountIso(_isoPath);
                    }
                }
            }
            catch { /* cai no fallback 7z */ }

            string sevenZipPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "App", "7Zip", "7z.exe");
            if (!File.Exists(sevenZipPath)) return "";

            // Extrai somente os membros sources/install.* (filtro por nome exato) - multi-threaded
            string args = $"x \"{_isoPath}\" -o\"{workDir}\" \"sources/install.esd\" \"sources/install.wim\" -y -mmt=on";
            AddLog($"7z (somente install.*): {args}");
            var (code, _) = await ExecuteShellCommand(sevenZipPath, args);

            if (File.Exists(existingWim)) return existingWim;
            if (File.Exists(existingEsd)) return existingEsd;

            // Fallback: sem filtro de membro (ISO com caminhos diferentes)
            args = $"x \"{_isoPath}\" -o\"{workDir}\" -y";
            await ExecuteShellCommand(sevenZipPath, args);
            var found = Directory.EnumerateFiles(workDir, "install.wim", SearchOption.AllDirectories).FirstOrDefault();
            if (found != null) return found;
            found = Directory.EnumerateFiles(workDir, "install.esd", SearchOption.AllDirectories).FirstOrDefault();
            return found ?? "";
        }

        // ==========================================
        // CREATE BUTTON - Mostra configuração
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

                if (_editions.Count == 0)
                {
                    mw.ShowInfo("ANÁLISE", "Analise a ISO primeiro para listar as edições (rápido, só o install.wim/esd).");
                }

                OverlayBusy.Visibility = Visibility.Collapsed;
                OverlayConfig.Visibility = Visibility.Visible;
                TxtConfigIsoInfo.Text = $"ISO: {Path.GetFileName(_isoPath)}\nEdições: {(_editions.Count == 0 ? "não analisadas" : $"{_editions.Count} - use o combo abaixo")}";
                UpdateModeHint();
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

        private void UpdateModeHint()
        {
            TxtModeHint.Text = "Modo: NATIVO (wimlib + registro offline, sem montar) - todos os recursos sem DISM";
        }

        private int SelectedEditionIndex()
        {
            if (ComboEditions.SelectedItem is WimEdition we) return we.Index;
            return 1;
        }

        private string SelectedCompression()
        {
            return ComboCompression.SelectedIndex == 1 ? "lzx" : "lzms";
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
            bool chkStripEdition = ChkStripEdition.IsChecked == true;
            bool chkRemoveDefaultStorePackages = ChkRemoveDefaultStorePackages.IsChecked == true;
            bool chkSetupComplete = ChkSetupComplete.IsChecked == true;
            bool chkConXLegacyFix = ChkConXLegacyFix.IsChecked == true;
            bool chkRemoveAI = ChkRemoveAI.IsChecked == true;

            int editionIndex = SelectedEditionIndex();
            string compression = SelectedCompression();

            OverlayConfig.Visibility = Visibility.Collapsed;
            ShowBusy("💿 KIT ISO EDITOR - EXECUTANDO");
            SetBusyStatus("Aplicando configurações...", 2, "Inicializando");
            UpdateModeHint();

            // Se o usuário pulou o botão ANALISAR, faz a análise aqui (rápida, sem extrair nada)
            if (_editions.Count == 0)
            {
                SetBusyStatus("Analisando a ISO (auto)...", 4, "Analisando a ISO");
                AddLog("Edições não analisadas - executando análise automática antes do build...");
                try
                {
                    var (ok, msg, editions, installPath) = await AnalyzeIsoMountedAsync();
                    if (!ok || editions.Count == 0 || string.IsNullOrEmpty(installPath))
                    {
                        // Fallback: extração 7z do install.* (máquina sem mount de ISO)
                        string installFile = await ExtractInstallFileOnlyAsync();
                        if (string.IsNullOrEmpty(installFile) || !File.Exists(installFile))
                        {
                            mw.ShowError("ERRO", "Não foi encontrado sources\\install.wim ou install.esd na ISO.");
                            OverlayBusy.Visibility = Visibility.Collapsed;
                            return;
                        }
                        (ok, msg, editions) = await IsoEditorManager.AnalyzeWimAsync(installFile);
                        if (!ok || editions.Count == 0)
                        {
                            mw.ShowError("ERRO", msg);
                            OverlayBusy.Visibility = Visibility.Collapsed;
                            return;
                        }
                    }
                    _editions = editions;
                    ComboEditions.ItemsSource = _editions;
                    ComboEditions.SelectedIndex = 0;
                    editionIndex = SelectedEditionIndex();
                    AddLog(msg);
                }
                catch (Exception autoEx)
                {
                    mw.ShowError("ERRO", $"Falha na análise automática: {autoEx.Message}");
                    OverlayBusy.Visibility = Visibility.Collapsed;
                    return;
                }
            }

            string workDir = "";
            string isoContents = "";
            string driverExportDir = "";
            string workFile = ""; // install.wim/esd na área de trabalho

            try
            {
                workDir = GetIsoWorkFolder();
                isoContents = Path.Combine(workDir, "iso_contents");
                Directory.CreateDirectory(isoContents);

                // 1. Extrair conteúdo da ISO (monta a ISO + cópia nativa, reusa pasta persistente se já estiver completa)
                SetBusyStatus("Montando ISO e copiando conteúdo...", 8, "Copiando conteúdo da ISO");
                AddLog("Montando ISO e copiando conteúdo (estilo Titus, nativo)...");
                string existingInstall = Directory.EnumerateFiles(isoContents, "install.wim", SearchOption.AllDirectories).FirstOrDefault()
                                         ?? Directory.EnumerateFiles(isoContents, "install.esd", SearchOption.AllDirectories).FirstOrDefault();
                if (existingInstall != null && Directory.GetFiles(isoContents, "*", SearchOption.AllDirectories).Length > 50)
                {
                    AddLog("Conteúdo da ISO já extraído (reuso).");
                }
                else
                {
                    File.SetAttributes(_isoPath, FileAttributes.Normal);
                    var copyResult = await CopyDirectoryAsync(isoContents);
                    if (!copyResult.Success)
                    {
                        mw.ShowError("ERRO", $"Falha ao extrair ISO: {copyResult.Message}");
                        await CleanupIsoEdit(workDir, _isoPath);
                        return;
                    }
                    AddLog(copyResult.Message);
                }

                if (CheckCancelled()) return;

                // 2. Localizar install.wim / install.esd
                string wimPath = Directory.EnumerateFiles(isoContents, "install.wim", SearchOption.AllDirectories).FirstOrDefault()
                                 ?? Directory.EnumerateFiles(isoContents, "install.esd", SearchOption.AllDirectories).FirstOrDefault();
                if (wimPath == null)
                {
                    mw.ShowError("ERRO", "install.wim ou install.esd não encontrado na ISO.");
                    await CleanupIsoEdit(workDir, _isoPath);
                    return;
                }
                workFile = wimPath;
                AddLog($"Imagem de instalação: {wimPath}");
                File.SetAttributes(wimPath, FileAttributes.Normal);

                bool origIsEsd = wimPath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase);

                // Re-analisa o arquivo REAL em iso_contents: a pasta persistente pode conter
                // um install.wim ja exportado (1 imagem) de uma rodada anterior, enquanto
                // _editions ainda reflete a ISO original (N edicoes).
                var (fileOk, fileMsg, fileEditions) = await IsoEditorManager.AnalyzeWimAsync(wimPath);
                if (!fileOk || fileEditions.Count == 0)
                {
                    mw.ShowError("ERRO", $"Nao foi possivel analisar a imagem de instalacao: {fileMsg}");
                    await CleanupIsoEdit(workDir, _isoPath);
                    return;
                }
                int fileImageCount = fileEditions.Count;
                if (fileImageCount == 1 && editionIndex > 1)
                {
                    AddLog("install em iso_contents ja e imagem unica (export de rodada anterior). Usando edicao 1.");
                    editionIndex = 1;
                }

                // =====================================================
                // FLUXO NATIVO (wimlib + registro offline, SEM montar)
                // =====================================================
                {
                    System.Diagnostics.Stopwatch flowSw = System.Diagnostics.Stopwatch.StartNew();
                    // Tamanho do WIM APOS o export (antes de qualquer modificacao): se nada
                    // mudar ate o optimize (reuso puro), o optimize e desnecessario - a
                    // reconstrucao estrutural so ganha espaco quando houve deletes/updates.
                    long wimSizeBeforeTweaks = 0;
                    // Strip/export (wimlib export) - converte ESD->WIM quando necessário.
                    // Skip: WIM de edição ÚNICA já exportado em rodada anterior (pasta persistente).
                    bool alreadySingle = !origIsEsd && fileImageCount == 1;
                    if ((chkStripEdition || origIsEsd) && !alreadySingle)
                    {
                        SetBusyStatus($"Exportando edição {editionIndex} (wimlib, compressão {compression})...", 18, "Exportando edição");
                        AddLog($"wimlib export da edição {editionIndex} -> install.wim ({compression})...");
                        string srcWim = wimPath;
                        string destWim = Path.Combine(Path.GetDirectoryName(wimPath) ?? isoContents, "install.wim");
                        var exp = await IsoEditorManager.ExportSingleEditionAsync(srcWim, editionIndex, destWim, compression);
                        if (!exp.Success)
                        {
                            mw.ShowError("ERRO", $"Falha ao exportar edição: {exp.Message}");
                            await CleanupIsoEdit(workDir, _isoPath);
                            return;
                        }
                        AddLog($"{exp.Message} ({flowSw.Elapsed.TotalSeconds:0}s de fluxo nativo)");
                        wimPath = destWim;
                        if (origIsEsd && srcWim != wimPath)
                        {
                            try { File.Delete(srcWim); } catch { }
                            // rename install.wim sobre o install.esd original (setup espera install.wim)
                            string esdOrig = Path.Combine(Path.GetDirectoryName(wimPath) ?? isoContents, "install.esd");
                            try
                            {
                                if (File.Exists(esdOrig)) File.Delete(esdOrig);
                                // ensure filename install.wim kept on sources
                            }
                            catch { }
                        }
                        workFile = wimPath;
                        editionIndex = 1; // após o export, o WIM é imagem única
                    }

                    // Baseline do tamanho: o WIM "cru" de agora (pos-export). Se o fluxo abaixo
                    // nao modificar o arquivo, o optimize e pulado (nada a reconstruir).
                    try { wimSizeBeforeTweaks = new FileInfo(wimPath).Length; } catch { }

                    // Registry tweaks via wimlib (extract hive -> reg load -> add -> unload -> re-inject)
                    var edits = BuildRegistryEdits(chkBypassRequirements, chkDisableSponsoredApps, chkDisableTelemetry,
                        chkDisableOneDrive, chkDisableCopilot, chkDisableUpdateOOBE, chkDisableTeams, chkDisableOutlook,
                        chkDisableBitLocker, chkDisableChat, chkDisableReservedStorage, chkRemoveDefaultStorePackages, chkRemoveAI);
                    if (edits.Count > 0)
                    {
                        if (CheckCancelled()) return;
                        SetBusyStatus("Aplicando registry tweaks (wimlib, sem montar)...", 35, "Aplicando registry tweaks");
                        AddLog($"Aplicando {edits.Count} registry tweaks sem montar...");
                        var regResult = await IsoEditorManager.ApplyRegistryEditsNoMountAsync(wimPath, editionIndex, edits, AddLog);
                        AddLog(regResult.Message);
                        if (!regResult.Success) mw.ShowInfo("AVISO", regResult.Message);
                    }

                    // Bloat AppX (wimlib dir + update delete + hive SOFTWARE Deprovisioned) - SEM DISM.
                    // Lista expandida no estilo Chris Titus (winutil 2026): 42 prefixos de pacote.
                    if (chkDebloatPreset)
                    {
                        if (CheckCancelled()) return;
                        var bloatPrefixes = new List<string>
                        {
                            // IA / assistente
                            "Microsoft.Copilot", "Microsoft.549981C3F5F10",
                            // Produtividade / Office
                            "Clipchamp.Clipchamp", "Microsoft.MicrosoftOfficeHub", "Microsoft.MicrosoftTeams",
                            "Microsoft.OutlookForWindows", "Microsoft.PowerAutomateDesktop", "Microsoft.Todos",
                            // Comunicação
                            "Microsoft.People", "Microsoft.WindowsCommunicationsApps", "MSTeams",
                            // Entretenimento / mídia
                            "Microsoft.ZuneMusic", "Microsoft.WindowsSoundRecorder",
                            "Microsoft.MicrosoftSolitaireCollection", "Microsoft.MicrosoftStickyNotes",
                            "Microsoft.Getstarted",
                            // Busca / informação
                            "Microsoft.BingNews", "Microsoft.BingSearch", "Microsoft.BingWeather",
                            // Utilitários
                            "Microsoft.WindowsAlarms", "Microsoft.WindowsCalculator", "Microsoft.WindowsCamera",
                            "Microsoft.WindowsClock", "Microsoft.WindowsMaps", "Microsoft.WindowsPhotos",
                            "Microsoft.WindowsScan", "Microsoft.ScreenSketch", "Microsoft.MixedReality.Portal",
                            "Microsoft.WindowsFeedbackHub", "Microsoft.GetHelp", "Microsoft.Wallet",
                            "Microsoft.PPIProjection",
                            // Sistema / dev
                            "Microsoft.Windows.DevHome", "Microsoft.StartExperiencesApp", "Microsoft.Paint",
                            "MicrosoftCorporationII.QuickAssist",
                            // Phone Link
                            "Microsoft.Windows.Phone", "Microsoft.YourPhone",
                            // Xbox
                            "Microsoft.XboxApp", "Microsoft.XboxGamingOverlay", "Microsoft.XboxIdentityProvider",
                            "Microsoft.XboxSpeechToTextOverlay"
                        };
                        SetBusyStatus("Removendo AppX bloatware (wimlib, sem montar)...", 45, "Removendo AppX bloatware");
                        AddLog($"Removendo {bloatPrefixes.Count} AppX provisionados sem DISM (wimlib + registro offline)...");
                        var debloatResult = await IsoEditorManager.RemoveProvisionedAppsNoMountAsync(wimPath, editionIndex, bloatPrefixes, AddLog);
                        AddLog(debloatResult.Message);
                        if (!debloatResult.Success) mw.ShowInfo("AVISO", debloatResult.Message);
                    }

                    // Scheduled tasks (wimlib update delete) - SEM DISM
                    {
                        if (CheckCancelled()) return;
                        SetBusyStatus("Deletando scheduled tasks (wimlib, sem montar)...", 50, "Deletando scheduled tasks");
                        AddLog("Deletando arquivos de scheduled tasks via wimlib update...");
                        var taskPaths = new List<string>
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
                        var tasksResult = await IsoEditorManager.DeleteScheduledTaskFilesNoMountAsync(wimPath, editionIndex, taskPaths, AddLog);
                        AddLog(tasksResult.Message);
                        if (!tasksResult.Success) mw.ShowInfo("AVISO", tasksResult.Message);
                    }

                    // Injetar arquivos soltos no WIM (ei.cfg já no sources; aqui fica para autounattend-like)
                    // (Os arquivos soltos da media são adicionados ao diretório isoContents mais abaixo.)

                    // SetupComplete.cmd (2026): roda com privilégio SYSTEM logo após o setup,
                    // injetado no WIM via wimlib (sem montar). Lança a automação KitLugia no 1º boot
                    // sem depender de RunOnce/Startup. Referência: MS Learn "Add a Custom Script to Windows Setup".
                    if (chkSetupComplete)
                    {
                        string setupComplete = Path.Combine(Path.GetTempPath(), $"KitLugia_SetupComplete_{DateTime.Now:yyyyMMdd_HHmmss}.cmd");
                        await File.WriteAllTextAsync(setupComplete,
                            "@echo off\r\n" +
                            "rem KitLugia ISO Editor - SetupComplete.cmd (roda pos-setup, privilegio SYSTEM)\r\n" +
                            "rem Garante que o bootstrap rode no primeiro boot apos a instalacao.\r\n" +
                            "for %%i in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (\r\n" +
                            "    if exist %%i:\\_KitLugiaSetup\\bootstrap.bat (\r\n" +
                            "        echo [KitLugia] Executando KitLugiaSetup de %%i:\\_KitLugiaSetup\\bootstrap.bat\r\n" +
                            "        call %%i:\\_KitLugiaSetup\\bootstrap.bat\r\n" +
                            "        goto :done\r\n" +
                            "    )\r\n" +
                            ")\r\n" +
                            ":done\r\n" +
                            "exit /b 0\r\n");
                        SetBusyStatus("Injetando SetupComplete.cmd (2026)...", 60, "Injetando SetupComplete.cmd");
                        AddLog("Injetando SetupComplete.cmd no WIM (roda bootstrap no 1º boot)...");
                        bool scOk = await IsoEditorManager.InjectFilesIntoWimAsync(wimPath, editionIndex,
                            new[] { (setupComplete, "/Windows/Setup/Scripts/SetupComplete.cmd") });
                        AddLog(scOk ? "SetupComplete.cmd injetado (wimlib)." : "Aviso: falha ao injetar SetupComplete.cmd.");
                        try { File.Delete(setupComplete); } catch { }
                    }

                    // Fix ConX 24H2/25H2 (Win11IsoBuilder 2026): injeta winpeshl.ini no boot.wim
                    // (index 2 = WinPE setup) que força setup.exe com /legacy - restaura instalação
                    // desatendida nas versões que ignoram o unattend.xml/autounattend via ConX.
                    if (chkConXLegacyFix)
                    {
                        string bootWimPath = Path.Combine(isoContents, "sources", "boot.wim");
                        if (File.Exists(bootWimPath))
                        {
                            string winpeshl = Path.Combine(Path.GetTempPath(), $"KitLugia_winpeshl_{DateTime.Now:yyyyMMdd_HHmmss}.ini");
                            // Conteúdo exato conhecido (ElevenForum 24H2/25H2 + NTLite "Boot/Setup Legacy"):
                            // [LaunchApps] com vírgula separando o argumento.
                            await File.WriteAllTextAsync(winpeshl,
                                "[LaunchApps]\r\n" +
                                "%SystemDrive%\\sources\\setup.exe, /legacy\r\n");
                            SetBusyStatus("Aplicando fix ConX 24H2/25H2 (boot.wim)...", 63, "Aplicando fix ConX");
                            AddLog("Aplicando fix ConX 24H2/25H2 no boot.wim (winpeshl.ini -> setup.exe /legacy)...");
                            bool cxOk = await IsoEditorManager.InjectFilesIntoWimAsync(bootWimPath, 2,
                                new[] { (winpeshl, "/Windows/System32/winpeshl.ini") });
                            AddLog(cxOk ? "Fix ConX aplicado (wimlib update no boot.wim)." : "Aviso: falha ao aplicar fix ConX no boot.wim.");
                            try { File.Delete(winpeshl); } catch { }
                        }
                        else AddLog("Aviso: boot.wim não encontrado em sources\\ - fix ConX ignorado.");
                    }

                    // Drivers: $WinPEDriver$ na raiz da mídia (método MS Learn - o Setup.exe do WinPE
                    // varre recursivamente *.inf e injeta no driverstore do OS instalado) - SEM DISM
                    if (chkInjectDrivers)
                    {
                        if (CheckCancelled()) return;
                        SetBusyStatus("Exportando drivers do sistema (pnputil)...", 55, "Exportando drivers");
                        AddLog("Exportando drivers do sistema atual (pnputil)...");
                        driverExportDir = Path.Combine(workDir, "driver_export");
                        Directory.CreateDirectory(driverExportDir);
                        var exportResult = await ExportWindowsDriversPnputil(driverExportDir);
                        AddLog(exportResult.Message);
                        if (exportResult.Success)
                        {
                            string winPeDriverDir = Path.Combine(isoContents, "$WinPEDriver$");
                            Directory.CreateDirectory(winPeDriverDir);
                            int copied = 0;
                            foreach (var file in Directory.EnumerateFiles(driverExportDir, "*", SearchOption.AllDirectories))
                            {
                                try { File.Copy(file, Path.Combine(winPeDriverDir, Path.GetFileName(file)), true); copied++; }
                                catch { }
                            }
                            AddLog($"Drivers copiados para $WinPEDriver$ ({copied} arquivos). O Setup.exe injeta no driverstore automaticamente.");
                        }
                    }

                    // Otimização final do WIM (wimlib optimize) - substitui o DISM /ResetBase,
                    // inútil em mídia nova (não há WinSxS\Backup para limpar). SEM --compress:
                    // recompressão (lenta) é desnecessária - o export já comprimiu com a escolha.
                    if (chkCleanupWinSxS)
                    {
                        long wimSizeNow = 0;
                        try { wimSizeNow = new FileInfo(wimPath).Length; } catch { }
                        // Reuso puro: nenhuma etapa mudou o WIM (tweaks sem deletes/bloat inexistente/
                        // tasks ja ausentes) - optimize nao reconstruiria nada; pula a reconstrucao.
                        if (wimSizeNow == wimSizeBeforeTweaks && wimSizeBeforeTweaks > 0)
                        {
                            AddLog("WIM inalterado nesta rodada (reuso) - optimize desnecessario, pulando.");
                        }
                        else
                        {
                            SetBusyStatus("Otimizando WIM (wimlib optimize, sem recompressão)...", 70, "Otimizando WIM");
                            AddLog("Otimizando WIM via wimlib optimize (remove espaço dos updates)...");
                            var optResult = await IsoEditorManager.OptimizeWimAsync(wimPath);
                            AddLog($"{optResult.Message} ({flowSw.Elapsed.TotalSeconds:0}s de fluxo nativo)");
                            if (!optResult.Success) mw.ShowInfo("AVISO", optResult.Message);
                        }
                    }
                    AddLog($"Tempo total do fluxo nativo: {flowSw.Elapsed.TotalSeconds:0}s.");
                }

                // =====================================================
                // ARQUIVOS SOLTOS NA MÍDIA
                // =====================================================
                if (chkDisableSponsoredApps)
                {
                    // ei.cfg força Retail (evita bloat marcado pela edição)
                    string sourcesDir = Path.Combine(isoContents, "sources");
                    try
                    {
                        Directory.CreateDirectory(sourcesDir);
                        File.WriteAllText(Path.Combine(sourcesDir, "ei.cfg"), "[Channel]\r\nRetail\r\n");
                        AddLog("ei.cfg (Retail) criado em sources\\.");

                        // PID.txt stale da mídia original: referencia a build original e pode
                        // dar "PID.txt invalid" no setup após a mídia ser modificada (Titus deleta).
                        string pidFile = Path.Combine(sourcesDir, "PID.txt");
                        if (File.Exists(pidFile))
                        {
                            try
                            {
                                File.Delete(pidFile);
                                AddLog("PID.txt removido (mídia modificada - evitando erro de PID no setup).");
                            }
                            catch (Exception pidEx) { AddLog($"Aviso: PID.txt -> {pidEx.Message}"); }
                        }
                    }
                    catch (Exception ex) { AddLog($"Aviso: ei.cfg -> {ex.Message}"); }
                }

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

                // KitLugiaSetup + .kitlugia
                SetBusyStatus("Adicionando KitLugia à ISO...", 75, "Adicionando KitLugia");
                AddLog("Adicionando KitLugiaSetup para automação pós-instalação...");
                await AddKitLugiaSetupAsync(isoContents);

                // =====================================================
                // CRIAÇÃO DA ISO FINAL (BOOTÁVEL)
                // =====================================================
                SetBusyStatus("Criando ISO final (bootável BIOS+UEFI)...", 85, "Criando ISO final");
                AddLog("Criando ISO final com oscdimg (boot dual)...");
                var createResult = await IsoEditorManager.CreateIso(isoContents, _isoDestPath);
                if (createResult.Success)
                {
                    SetBusyStatus("ISO criada com sucesso!", 100, "Concluído");
                    TxtStatus.Text = "✅. ISO criada com sucesso!";
                    AddLog($"ISO criada com sucesso em: {_isoDestPath}");
                    mw.ShowSuccess("SUCESSO", $"ISO criada com sucesso!\nDestino: {_isoDestPath}");
                    OverlayBusy.Visibility = Visibility.Collapsed;
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
                await CleanupIsoEdit(workDir, _isoPath);
            }
        }

        /// <summary>
        /// Monta a lista de tweaks de registro usando o mapeamento hive/rota (sem prefixo HKLM\z).
        /// reutiliza o MESMO mapeamento do fluxo DISM legado, só que otimizado para o no-mount.
        /// </summary>
        private List<(string Hive, string Key, string Name, string Type, string Value)> BuildRegistryEdits(
            bool bypassRequirements, bool disableSponsoredApps, bool disableTelemetry, bool disableOneDrive,
            bool disableCopilot, bool disableUpdateOOBE, bool disableTeams, bool disableOutlook,
            bool disableBitLocker, bool disableChat, bool disableReservedStorage,
            bool removeDefaultStorePackages = false, bool disableAI = false)
        {
            var list = new List<(string, string, string, string, string)>();

            if (bypassRequirements)
            {
                list.Add(("SYSTEM", "Setup\\LabConfig", "BypassCPUCheck", "REG_DWORD", "1"));
                list.Add(("SYSTEM", "Setup\\LabConfig", "BypassRAMCheck", "REG_DWORD", "1"));
                list.Add(("SYSTEM", "Setup\\LabConfig", "BypassSecureBootCheck", "REG_DWORD", "1"));
                list.Add(("SYSTEM", "Setup\\LabConfig", "BypassStorageCheck", "REG_DWORD", "1"));
                list.Add(("SYSTEM", "Setup\\LabConfig", "BypassTPMCheck", "REG_DWORD", "1"));
                list.Add(("SYSTEM", "Setup\\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU", "REG_DWORD", "1"));
            }
            if (disableSponsoredApps)
            {
                list.Add(("NTUSER", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "OemPreInstalledAppsEnabled", "REG_DWORD", "0"));
                list.Add(("NTUSER", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "PreInstalledAppsEnabled", "REG_DWORD", "0"));
                list.Add(("NTUSER", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "SilentInstalledAppsEnabled", "REG_DWORD", "0"));
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsConsumerFeatures", "REG_DWORD", "1"));
            }
            if (disableTelemetry)
            {
                list.Add(("NTUSER", "Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo", "Enabled", "REG_DWORD", "0"));
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Windows\\DataCollection", "AllowTelemetry", "REG_DWORD", "0"));
            }
            if (disableOneDrive)
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Windows\\OneDrive", "DisableFileSyncNGSC", "REG_DWORD", "1"));
            if (disableCopilot)
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot", "REG_DWORD", "1"));
            if (disableUpdateOOBE)
            {
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Windows\\WindowsUpdate\\AU", "NoAutoUpdate", "REG_DWORD", "1"));
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Windows\\WindowsUpdate", "DisableWindowsUpdateAccess", "REG_DWORD", "1"));
            }
            if (disableTeams)
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Teams", "DisableInstallation", "REG_DWORD", "1"));
            if (disableOutlook)
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Windows\\Windows Mail", "PreventRun", "REG_DWORD", "1"));
            if (disableBitLocker)
                list.Add(("SYSTEM", "ControlSet001\\Control\\BitLocker", "PreventDeviceEncryption", "REG_DWORD", "1"));
            if (disableChat)
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Windows\\Windows Chat", "ChatIcon", "REG_DWORD", "3"));
            if (disableReservedStorage)
                list.Add(("SOFTWARE", "Microsoft\\Windows\\CurrentVersion\\ReserveManager", "ShippedWithReserves", "REG_DWORD", "0"));
            if (removeDefaultStorePackages)
                // Política 24H2/25H2: RemoveDefaultMicrosoftStorePackages (DWORD 1) remove os apps
                // padrão instalados da Store no provisionamento; sobrevive a feature updates.
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Windows\\Appx", "RemoveDefaultMicrosoftStorePackages", "REG_DWORD", "1"));
            if (disableAI)
            {
                // Copilot 25H2 -> TurnOffWindowsCopilot (política), SEM DISM (no-mount via wimlib)
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot", "REG_DWORD", "1"));
                // Windows Recall 24H2/25H2 -> chaves documentadas (MS Learn "Manage Recall for Windows
                // clients"): DisableAIDataAnalysis desliga os snapshots; AllowRecallEnablement=0
                // coloca o componente Recall em estado desabilitado/removido. SEM DISM.
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Windows\\WindowsAI", "DisableAIDataAnalysis", "REG_DWORD", "1"));
                list.Add(("SOFTWARE", "Policies\\Microsoft\\Windows\\WindowsAI", "AllowRecallEnablement", "REG_DWORD", "0"));
                list.Add(("NTUSER", "Software\\Policies\\Microsoft\\Windows\\WindowsAI", "DisableAIDataAnalysis", "REG_DWORD", "1"));
            }

            return list;
        }

        private async Task AddKitLugiaSetupAsync(string isoContents)
        {
            try
            {
                string kitLugiaSetup = Path.Combine(isoContents, "_KitLugiaSetup");
                await Task.Run(() => Directory.CreateDirectory(kitLugiaSetup));

                string kitLugiaExe = Environment.ProcessPath
                                     ?? System.Reflection.Assembly.GetExecutingAssembly().Location
                                     ?? AppContext.BaseDirectory.TrimEnd('\\') + "\\KitLugia.GUI.exe";
                if (File.Exists(kitLugiaExe))
                {
                    await Task.Run(() => File.Copy(kitLugiaExe, Path.Combine(kitLugiaSetup, "KitLugia.exe"), true));
                    AddLog("KitLugia.exe copiado para ISO.");
                }
                else AddLog("Aviso: Não foi possível localizar KitLugia.exe para cópia.");

                string configJson = "{\n  \"AutoRun\": true,\n  \"Source\": \"ISO_Automation\"\n}";
                await Task.Run(() => File.WriteAllText(Path.Combine(kitLugiaSetup, "config.json"), configJson));

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

                string kitlugiaId = "# KitLugia ISO Identifier\n# Esta ISO foi criada pelo KitLugia ISO Editor\n\nKitLugiaISO=true\nVersion=1.0\nCreated="
                                    + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                                    + "\nSource=KitLugia_ISO_Editor\nPreserveAutounattend=true\nAllowUserConfig=true\n";
                await Task.Run(() => File.WriteAllText(Path.Combine(isoContents, ".kitlugia"), kitlugiaId));
                AddLog("KitLugiaSetup + .kitlugia criados.");
            }
            catch (Exception ex)
            {
                AddLog($"Aviso: KitLugiaSetup -> {ex.Message}");
            }
        }

        private async Task<(bool Success, string Message)> ExportWindowsDriversPnputil(string destDir)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    // pnputil é o utilitário nativo de driver store do Windows (rápido, sem DISM):
                    // exporta todos os drivers de terceiros do store para destDir.
                    var psi = new ProcessStartInfo
                    {
                        FileName = "pnputil.exe",
                        Arguments = $"/export-driver * \"{destDir}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    int count = Directory.Exists(destDir) ? Directory.GetFiles(destDir, "*", SearchOption.AllDirectories).Length : 0;
                    if (process.ExitCode == 0) return (true, $"Drivers exportados com pnputil ({count} arquivos).");
                    return (false, $"Erro ao exportar drivers (pnputil): {error.Trim()}");
                }
                catch (Exception ex)
                {
                    return (false, $"Exceção ao exportar drivers: {ex.Message}");
                }
            });
        }

        private async Task<(bool Success, string Message)> CopyDirectoryAsync(string destDir)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    // Estilo Chris Titus (winutil ISO Creator): monta a ISO no Windows e copia
                    // o conteúdo do drive virtual com cópia nativa (ISO UDF é cru - o 7z só
                    // adiciona overhead de parsing; cópia direta é muito mais rápida).
                    var (mOk, mMsg, drive) = await Core.IsoManager.MountIso(_isoPath);
                    if (mOk && !string.IsNullOrWhiteSpace(drive))
                    {
                        try
                        {
                            string src = $"{drive.TrimEnd('\\', ':')}:\\";
                            string robocopyArgs = $"\"{src}\" \"{destDir}\" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP /MT:8";
                            var psi = new ProcessStartInfo("robocopy.exe", robocopyArgs)
                            {
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using var proc = Process.Start(psi)!;
                            string output = await proc.StandardOutput.ReadToEndAsync();
                            string error = await proc.StandardError.ReadToEndAsync();
                            await proc.WaitForExitAsync();
                            // robocopy: exit 0-7 = sucesso (1 = arquivos copiados)
                            if (proc.ExitCode <= 7)
                                return (true, $"ISO montada em {src} e conteúdo copiado (robocopy, código {proc.ExitCode}).");
                            return (false, $"robocopy falhou (código {proc.ExitCode}): {error}");
                        }
                        finally
                        {
                            await Core.IsoManager.DismountIso(_isoPath);
                        }
                    }

                    // Fallback: extração 7z (máquina sem mount de ISO / mount falhou)
                    string sevenZipPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "App", "7Zip", "7z.exe");
                    if (!File.Exists(sevenZipPath)) return (false, $"7-Zip não encontrado em {sevenZipPath}");

                    // -mmt=on: extração multi-threaded (padrão NTLite 2026) - acelera ISO de ~10GB
                    string args = $"x \"{_isoPath}\" -o\"{destDir}\" -y -mmt=on";
                    var (extCode, extOut) = await ExecuteShellCommand(sevenZipPath, args);
                    if (extCode != 0 && extCode != 1) return (false, $"Erro 7-Zip (Código {extCode}): {extOut}");
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

        private async Task CleanupIsoEdit(string workDir, string isoPath)
        {
            try
            {
                // Pasta de trabalho é persistente (reuso entre execuções); sem Delete aqui.
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private void AddLog(string message)
        {
            TxtLogViewer.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            TxtLogViewer.ScrollToEnd();
            AddOpLog(message);
        }

        #region Busy Overlay (progresso real estilo WinpeToolsPage/UpdatePage)

        private CancellationTokenSource? _cts;

        private void ShowBusy(string title)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            OverlayBusy.Visibility = Visibility.Visible;
            TxtOpTitle.Text = title;
            TxtOpDesc.Inlines.Clear();
            TxtProgressPercent.Text = "0%";
            TxtProgressStep.Text = "Inicializando...";
            TxtProgressStatus.Text = "Aguarde...";
            ProgressFill.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
            ProgressFill.Width = 0;
        }

        private void SetBusyStatus(string status, int pct, string stepLabel)
        {
            TxtProgressStatus.Text = status;
            TxtProgressStep.Text = stepLabel;
            ProgressFill.Width = Math.Min(pct / 100.0, 1.0) * 480.0;
            TxtProgressPercent.Text = $"{Math.Min(pct, 100)}%";
        }

        private bool CheckCancelled()
        {
            if (_cts == null || !_cts.IsCancellationRequested) return false;
            AddLog("Operação cancelada pelo usuário.");
            OverlayBusy.Visibility = Visibility.Collapsed;
            _isIsoEditorOperation = false;
            return true;
        }

        private void AddOpLog(string text)
        {
            var color = IsErrorText(text)
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x3D, 0x00))
                : text.Contains("✅") || text.Contains("sucesso", StringComparison.OrdinalIgnoreCase)
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
            TxtOpDesc.Inlines.Add(new LineBreak());
            TxtOpDesc.Inlines.Add(new Run(text) { Foreground = color });
            ScrollOverlayToBottom();
        }

        private void ScrollOverlayToBottom()
        {
            if (TxtOpDesc.Parent is ScrollViewer sv) sv.ScrollToBottom();
            else if (TxtOpDesc.Parent is Border b && b.Child is ScrollViewer sv2) sv2.ScrollToBottom();
        }

        private static bool IsErrorText(string text)
            => text.Contains("Erro", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Falha", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("❌");

        private void BtnCancelOp_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            TxtProgressStatus.Text = "Cancelando... (após a etapa atual)";
        }

        private void TxtCopyOpLog_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var inline in TxtOpDesc.Inlines)
            {
                if (inline is Run r) sb.AppendLine(r.Text);
            }
            if (sb.Length > 0)
            {
                try { System.Windows.Clipboard.SetText(sb.ToString()); TxtCopyOpLog.Text = "✅ Log copiado"; }
                catch { }
            }
        }

        #endregion

        // ==========================================
        // BASIC OPERATIONS
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
            ChkStripEdition.IsChecked = true;
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
            ChkCleanupWinSxS.IsChecked = false; // Não marcar /ResetBase por padrão (causa travamentos)
            ChkRemoveSupportFolder.IsChecked = true;
            ChkInjectDrivers.IsChecked = false;
            ChkDebloatPreset.IsChecked = false;
            ChkRemoveAI.IsChecked = false;
            ChkRemoveDefaultStorePackages.IsChecked = true;
            ChkSetupComplete.IsChecked = true;
            ChkConXLegacyFix.IsChecked = true;
            UpdateModeHint();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            ChkStripEdition.IsChecked = false;
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
            ChkRemoveAI.IsChecked = false;
            ChkRemoveDefaultStorePackages.IsChecked = false;
            ChkSetupComplete.IsChecked = false;
            ChkConXLegacyFix.IsChecked = false;
            UpdateModeHint();
        }

        private async void BtnCleanup_Click(object sender, RoutedEventArgs e)
        {
            if (_isIsoEditorOperation) return;
            _isIsoEditorOperation = true;
            try
            {
                var mw = Application.Current.MainWindow as MainWindow;
                if (mw == null) return;

                ShowBusy("🧹 KIT ISO EDITOR - LIMPANDO");
                SetBusyStatus("Limpando lixo do DISM/WIM...", 50, "Limpando lixo");
                var result = await IsoManager.CleanupDismWim();
                OverlayBusy.Visibility = Visibility.Collapsed;

                if (result.Success) mw.ShowInfo("Limpeza Concluída", result.Message);
                else mw.ShowError("Erro na Limpeza", result.Message);
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

        // ==========================================
        // KIT ISO STUDIO — Expansor
        // ==========================================
        private void BtnIsoStudio_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_isoPath) || !File.Exists(_isoPath))
            {
                var mw2 = Application.Current.MainWindow as MainWindow;
                mw2?.ShowInfo("ISO", "Selecione uma ISO primeiro para abrir o Estúdio.");
                return;
            }
            // Abre como Window separada para cobrir o kit inteiro como o PathManager do Guardian (legível, sem blur)
            var win = new Windows.KitIsoStudioWindow { Owner = Application.Current.MainWindow };
            bool? res = win.ShowDialog();
            if (res == true)
            {
                // Sincroniza escolhas do Studio com o painel padrão
                try
                {
                    // Exemplo: se usuário interagiu no Studio, marca flags equivalentes no painel padrão
                    // O Studio é um expansor visual — a lógica real de ISO continua no fluxo nativo
                    TxtStatus.Text = "🧬 KIT ISO STUDIO aplicado — expansor integrado";
                }
                catch { }
                OverlayConfig.Visibility = Visibility.Visible;
                TxtConfigIsoInfo.Text = $"ISO: {Path.GetFileName(_isoPath)} — Estúdio aplicado";
                UpdateModeHint();
            }
        }

        private void BtnCloseIsoStudio_Click(object sender, RoutedEventArgs e)
        {
            OverlayIsoStudio.Visibility = Visibility.Collapsed;
        }

        private void BtnApplyIsoStudio_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ChkStudioInjectDrivers?.IsChecked == true && !string.IsNullOrEmpty(TxtStudioDriverFolder?.Text) && TxtStudioDriverFolder.Text != "Nenhuma pasta selecionada")
                    ChkInjectDrivers.IsChecked = true;
                if (!string.IsNullOrWhiteSpace(TxtStudioReg?.Text) && TxtStudioReg.Text.Contains("[HKEY"))
                    ChkRemoveAI.IsChecked = true;
            }
            catch { }
            OverlayIsoStudio.Visibility = Visibility.Collapsed;
            OverlayConfig.Visibility = Visibility.Visible;
            TxtConfigIsoInfo.Text = $"ISO: {Path.GetFileName(_isoPath)} — Estúdio aplicado";
            UpdateModeHint();
        }

        private void BtnStudioPickDriverFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new FolderBrowserDialog { Description = "Selecione a pasta com drivers (.inf)" };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TxtStudioDriverFolder.Text = dlg.SelectedPath;
                ChkStudioInjectDrivers.IsChecked = true;
            }
        }

        private void BtnIsoStudioApplyDebloat_Click(object sender, RoutedEventArgs e)
        {
            ChkDebloatPreset.IsChecked = true;
            UpdateModeHint();
            var mw = Application.Current.MainWindow as MainWindow;
            mw?.ShowInfo("Estúdio", "Preset de 40+ AppX marcado. Você pode refinar individualmente no Estúdio.");
        }

        public void Cleanup()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _isoDestPath = string.Empty;
            this.Unloaded -= IsoEditorPage_Unloaded;
            this.DataContext = null;
        }
    }
}
