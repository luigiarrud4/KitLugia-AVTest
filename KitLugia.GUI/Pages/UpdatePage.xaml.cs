using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Thickness = System.Windows.Thickness;

namespace KitLugia.GUI.Pages
{
    public partial class UpdatePage : Page
    {
        private GitHubUpdater.ReleaseInfo? _latestRelease;
        private bool _isUpdateOperation;

        public UpdatePage()
        {
            InitializeComponent();
            Loaded += UpdatePage_Loaded;

            Unloaded += UpdatePage_Unloaded;

            // ✅. Carregar informações após inicialização completa
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await LoadCurrentVersionInfoAsync();
                }
                catch (Exception ex)
                {
                    KitLugia.Core.Logger.Log($"UpdatePage: Error loading version info: {ex.Message}");
                }
            });
        }


        public void Cleanup()
        {
            // Limpar panel de info e desregistrar eventos
            CurrentInfoPanel?.Children?.Clear();
            Loaded -= UpdatePage_Loaded;
            Unloaded -= UpdatePage_Unloaded;


            this.DataContext = null;




        }

        private void UpdatePage_Loaded(object sender, RoutedEventArgs e)
        {
            // Event handler vazio - apenas para garantir que Loaded seja disparado
        }

        private void UpdatePage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private async Task LoadCurrentVersionInfoAsync()
        {
            try
            {
                // ✅. Mostrar status de carregamento
                CurrentVersionText.Text = "🔄 Carregando...";
                
                // Informações da versão atual
                var assembly = Assembly.GetExecutingAssembly();
                var assemblyVersion = assembly.GetName().Version?.ToString() ?? "1.0.0.0";
                

                var buildDate = System.IO.File.GetLastWriteTime(assembly.Location);
                
                // ✅. Adicionar timeout de 10 segundos para evitar congelamento
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                
                var versionInfo = await Task.Run(async () => 
                {
                    return await KitLugia.Core.SmartVersionDetector.GetVersionInfoAsync(buildDate);
                }, cts.Token);
                
                var localBuildDate = DateTimeOffset.Now; // Data/hora atual com timezone do usuário
                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";


                CurrentVersionText.Text = $"v{versionInfo.RealVersion}";
                

                CurrentBuildDateText.Text = $"📅 Compilado: {buildDate:dd/MM/yyyy HH:mm}";
                CurrentNowDateText.Text = $"🕐 Agora: {localBuildDate:dd/MM/yyyy HH:mm}";
                
                // Informações do sistema
                var runtimeVersion = Environment.Version.ToString();
                var platform = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                var timezone = TimeZoneInfo.Local.DisplayName;
                var utcOffset = DateTimeOffset.Now.Offset;
                

                CurrentTimezoneText.Text = $"🌍 Timezone: {timezone} ({utcOffset})";
                
                // Limpa e adiciona informações técnicas atualizadas
                CurrentInfoPanel.Children.Clear();
                CurrentInfoPanel.Children.Add(new TextBlock { Text = $"🔨 Build: {GetBuildType()}", FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 2) });
                CurrentInfoPanel.Children.Add(new TextBlock { Text = $"⚙️ Runtime: .NET {runtimeVersion}", FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 2) });
                CurrentInfoPanel.Children.Add(new TextBlock { Text = $"💻 Plataforma: Windows {platform}", FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 2) });
                CurrentInfoPanel.Children.Add(new TextBlock { Text = $"📄 Executável: {Path.GetFileName(exePath)}", FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 2) });
                

                CurrentInfoPanel.Children.Add(new TextBlock { Text = $"🔩 Assembly: {assemblyVersion}", FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 2) });
                CurrentInfoPanel.Children.Add(new TextBlock { Text = $"🔍 Detecção: {versionInfo.DetectionMethod}", FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 2) });
                CurrentInfoPanel.Children.Add(new TextBlock { Text = $"📦 Releases Online: {versionInfo.TotalReleases}", FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 2) });
                

                KitLugia.Core.Logger.Log($"📦 Versão DIN,MICA: Real={versionInfo.RealVersion}, Assembly={assemblyVersion}, Método={versionInfo.DetectionMethod}, Total={versionInfo.TotalReleases}");
            }
            catch (UnauthorizedAccessException ex)
            {
                // ✅. Erro de permissão - mostrar fallback
                CurrentVersionText.Text = "v2.0.5";
                CurrentBuildDateText.Text = $"📅 Compilado: {DateTime.Now:dd/MM/yyyy HH:mm}";
                CurrentNowDateText.Text = $"🕐 Agora: {DateTimeOffset.Now:dd/MM/yyyy HH:mm}";
                CurrentTimezoneText.Text = $"🌍 Timezone: {TimeZoneInfo.Local.DisplayName}";
                
                CurrentInfoPanel.Children.Clear();
                CurrentInfoPanel.Children.Add(new TextBlock { Text = "❌ Erro: Permissão de Administrador", FontSize = 12, Foreground = Brushes.Red, Margin = new Thickness(0, 2, 0, 2) });
                CurrentInfoPanel.Children.Add(new TextBlock { Text = "✅ Solução: Executar como Administrador", FontSize = 12, Foreground = Brushes.Red, Margin = new Thickness(0, 2, 0, 2) });
                CurrentInfoPanel.Children.Add(new TextBlock { Text = "📊 Status: Fallback Local", FontSize = 12, Foreground = Brushes.Orange, Margin = new Thickness(0, 2, 0, 2) });
                
                KitLugia.Core.Logger.Log($"✅ Erro de permissão: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // ✅. Timeout - mostrar fallback
                CurrentVersionText.Text = "v2.0.5";
                CurrentBuildDateText.Text = $"📅 Compilado: {DateTime.Now:dd/MM/yyyy HH:mm}";
                CurrentNowDateText.Text = $"🕐 Agora: {DateTimeOffset.Now:dd/MM/yyyy HH:mm}";
                CurrentTimezoneText.Text = $"🌍 Timezone: {TimeZoneInfo.Local.DisplayName}";
                
                CurrentInfoPanel.Children.Clear();
                CurrentInfoPanel.Children.Add(new TextBlock { Text = "🔍 Detecção: Timeout (10s)", FontSize = 12, Foreground = Brushes.Orange, Margin = new Thickness(0, 2, 0, 2) });
                CurrentInfoPanel.Children.Add(new TextBlock { Text = "📊 Status: Modo Offline", FontSize = 12, Foreground = Brushes.Orange, Margin = new Thickness(0, 2, 0, 2) });
                
                KitLugia.Core.Logger.Log("⏰ Timeout na detecção de versão - usando fallback");
            }
            catch (Exception ex)
            {
                // ✅. Erro - mostrar fallback
                CurrentVersionText.Text = "v2.0.5";
                CurrentBuildDateText.Text = $"📅 Compilado: {DateTime.Now:dd/MM/yyyy HH:mm}";
                CurrentNowDateText.Text = $"🕐 Agora: {DateTimeOffset.Now:dd/MM/yyyy HH:mm}";
                CurrentTimezoneText.Text = $"🌍 Timezone: {TimeZoneInfo.Local.DisplayName}";
                
                CurrentInfoPanel.Children.Clear();
                CurrentInfoPanel.Children.Add(new TextBlock { Text = $"❌ Erro: {ex.Message}", FontSize = 12, Foreground = Brushes.Red, Margin = new Thickness(0, 2, 0, 2) });
                CurrentInfoPanel.Children.Add(new TextBlock { Text = "📊 Status: Fallback Local", FontSize = 12, Foreground = Brushes.Red, Margin = new Thickness(0, 2, 0, 2) });
                
                KitLugia.Core.Logger.Log($"✅ Erro na detecção de versão: {ex.Message}");
            }
        }

        private string GetBuildType()
        {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                StatusText.Text = "Verificando atualizações...";
                StatusBorder.Background = new SolidColorBrush(Color.FromRgb(255, 167, 38)); // Laranja forte
                StatusText.Foreground = Brushes.White;
                CheckButton.IsEnabled = false;
                UpdateButton.IsEnabled = false;


                await GetReleaseDetails();
                
                if (_latestRelease != null)
                {

                    var currentVersion = GetCurrentVersion();
                    var latestVersion = ParseVersion(_latestRelease.TagName);
                    
                    KitLugia.Core.Logger.Log($"📦 Versão atual: {currentVersion}");
                    KitLugia.Core.Logger.Log($"📦 Versão latest: {latestVersion}");
                    

                    var hasUpdate = (_latestRelease.TagName == "Update" && currentVersion.Major == 1) || latestVersion > currentVersion;
                    
                    if (hasUpdate)
                    {
                        StatusText.Text = "✅. Nova versão disponível!";
                        StatusBorder.Background = new SolidColorBrush(Color.FromRgb(255, 215, 0)); // Dourado KitLugia
                        StatusText.Foreground = Brushes.Black;
                        UpdateButton.IsEnabled = true;
                        
                        StatusText.Text = $"🚀 Atualização disponível: {_latestRelease.Name}";
                        ManualDownloadLink.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        StatusText.Text = "✅. KitLugia está atualizado!";
                        StatusBorder.Background = new SolidColorBrush(Color.FromRgb(67, 160, 71)); // Verde forte
                        StatusText.Foreground = Brushes.White;
                        UpdateButton.IsEnabled = false;
                        ManualDownloadLink.Visibility = Visibility.Collapsed;
                        
                        LatestVersionText.Text = GetCurrentVersion().ToString();
                        LatestDateText.Text = "Você já está na versão mais recente";
                        LatestSizeText.Text = "N/A";
                        ReleaseNotesText.Text = "Nenhuma atualização disponível no momento.";
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"✅ Erro: {ex.Message}";
                StatusBorder.Background = new SolidColorBrush(Color.FromRgb(229, 57, 53)); // Vermelho forte
                StatusText.Foreground = Brushes.White;
                KitLugia.Core.Logger.Log($"Erro na verificação: {ex.Message}");
            }
            finally
            {
                CheckButton.IsEnabled = true;
            }
        }

        private void OpenDownloadLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_latestRelease?.HtmlUrl != null)
                {
                    // Abrir link do GitHub no navegador padrão
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _latestRelease.HtmlUrl,
                        UseShellExecute = true
                    });
                    
                    KitLugia.Core.Logger.Log($"🔗 Link de atualização aberto: {_latestRelease.HtmlUrl}");
                }
                else
                {
                    MessageBox.Show("Link de download não disponível. Tente verificar as atualizações novamente.", 
                        "Link Indisponível", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"Erro ao abrir link de download: {ex.Message}");
                MessageBox.Show($"Não foi possível abrir o link: {ex.Message}", 
                    "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GetReleaseDetails()
        {
            try
            {
                KitLugia.Core.Logger.Log("Buscando detalhes do release GitHub...");
                var response = await GitHubUpdater._httpClient.GetAsync(GitHubUpdater.ApiUrl);
                
                KitLugia.Core.Logger.Log($"📡 Status HTTP: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    KitLugia.Core.Logger.Log($"📄 JSON completo: {json}");
                    
                    _latestRelease = System.Text.Json.JsonSerializer.Deserialize<GitHubUpdater.ReleaseInfo>(json, GitHubUpdater.JsonOptions);
                    
                    if (_latestRelease != null)
                    {
                        KitLugia.Core.Logger.Log($"📦 TagName: '{_latestRelease.TagName}'");
                        KitLugia.Core.Logger.Log($"📦 Name: '{_latestRelease.Name}'");
                        KitLugia.Core.Logger.Log($"📦 Body: '{_latestRelease.Body}'");
                        KitLugia.Core.Logger.Log($"📦 PublishedAt: {_latestRelease.PublishedAt}");
                        KitLugia.Core.Logger.Log($"📦 Assets: {_latestRelease.Assets.Length}");
                        

                        var displayVersion = !string.IsNullOrEmpty(_latestRelease.TagName) ? _latestRelease.TagName : "Update";
                        LatestVersionText.Text = displayVersion;
                        

                        var displayDate = _latestRelease.PublishedAt != DateTime.MinValue 
                            ? _latestRelease.PublishedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm") + " (local)"
                            : "09/03/2026 01:49"; // Data do JSON
                        LatestDateText.Text = displayDate;
                        
                        // Tamanho do arquivo
                        if (_latestRelease.Assets?.Length > 0)
                        {
                            var sizeInMB = _latestRelease.Assets[0].Size / 1024.0 / 1024.0;
                            LatestSizeText.Text = $"Tamanho: {sizeInMB:F1} MB";
                            KitLugia.Core.Logger.Log($"📦 Asset: {_latestRelease.Assets[0].Name} - {sizeInMB:F1} MB");
                        }
                        else
                        {
                            LatestSizeText.Text = "N/A";
                            KitLugia.Core.Logger.Log("✅ Nenhum asset encontrado");
                        }
                        
                        // Notas da versão
                        ReleaseNotesText.Text = string.IsNullOrEmpty(_latestRelease.Body) 
                            ? "Nenhuma nota de versão disponível." 
                            : _latestRelease.Body;
                            
                        KitLugia.Core.Logger.Log($"Título: {_latestRelease.Name}");
                    }
                    else
                    {
                        KitLugia.Core.Logger.Log("✅ _latestRelease é null após deserialização");
                    }
                }
                else
                {
                    KitLugia.Core.Logger.Log($"✅ Erro HTTP: {response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ Erro ao buscar detalhes do release: {ex.Message}");
                KitLugia.Core.Logger.Log($"✅ Stack: {ex.StackTrace}");
                LatestVersionText.Text = "Erro";
                LatestDateText.Text = "--/--/---- --:--";
                LatestSizeText.Text = "N/A";
                ReleaseNotesText.Text = $"Erro ao carregar informações: {ex.Message}";
            }
        }

        private System.Version GetCurrentVersion()
        {
            try
            {

                var assembly = Assembly.GetExecutingAssembly();
                var buildDate = System.IO.File.GetLastWriteTime(assembly.Location);
                var realVersion = KitLugia.Core.SmartVersionDetector.GetRealVersion(buildDate);
                
                // Converter string de versão para Version object
                if (System.Version.TryParse(realVersion, out var version))
                {
                    KitLugia.Core.Logger.Log($"📦 Versão DIN,MICA detectada: {realVersion}");
                    return version;
                }
                
                // Fallback para assembly version
                var assemblyVersion = assembly.GetName().Version ?? new System.Version("2.0.5");
                KitLugia.Core.Logger.Log($"Usando assembly version: {assemblyVersion}");
                return assemblyVersion;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ Erro ao obter versão atual: {ex.Message}");
                return new System.Version("2.0.5"); // Fallback seguro
            }
        }

        private System.Version ParseVersion(string tag)
        {
            try
            {
                KitLugia.Core.Logger.Log($"ParseVersion input: '{tag}'");
                

                var cleanTag = tag;
                
                // Remove "KitLugia" e prefixos conhecidos
                if (cleanTag.StartsWith("KitLugia "))
                {
                    cleanTag = cleanTag.Substring(9); // Remove "KitLugia "
                }
                if (cleanTag.StartsWith("Release v"))
                {
                    cleanTag = cleanTag.Substring(9); // Remove "Release v"
                }
                else if (cleanTag.StartsWith("v"))
                {
                    cleanTag = cleanTag.Substring(1); // Remove "v"
                }
                
                // Remove sufixos conhecidos
                var suffixes = new[] { " Bugfix", " Release", " Beta", " Alpha", " Stable", " -", " " };
                foreach (var suffix in suffixes)
                {
                    var index = cleanTag.IndexOf(suffix);
                    if (index > 0)
                    {
                        cleanTag = cleanTag.Substring(0, index);
                        KitLugia.Core.Logger.Log($"Removido sufixo '{suffix}': '{cleanTag}'");
                    }
                }
                
                cleanTag = cleanTag.Trim();
                

                var versionMatch = System.Text.RegularExpressions.Regex.Match(cleanTag, @"^\d+\.\d+\.\d+");
                if (versionMatch.Success)
                {
                    var versionString = versionMatch.Value;
                    KitLugia.Core.Logger.Log($"YZ ParseVersion success: '{tag}' -> '{versionString}'");
                    return new System.Version(versionString);
                }
                
                // Se não encontrar pattern X.Y.Z, tenta X.Y
                versionMatch = System.Text.RegularExpressions.Regex.Match(cleanTag, @"^\d+\.\d+");
                if (versionMatch.Success)
                {
                    var versionString = versionMatch.Value + ".0"; // Adiciona .0 para completar
                    KitLugia.Core.Logger.Log($"YZ ParseVersion X.Y: '{tag}' -> '{versionString}'");
                    return new System.Version(versionString);
                }
                
                // Se não encontrou nada, tenta parse direto
                if (System.Version.TryParse(cleanTag, out var directVersion))
                {
                    KitLugia.Core.Logger.Log($"YZ ParseVersion direct: '{tag}' -> '{directVersion}'");
                    return directVersion;
                }
                
                KitLugia.Core.Logger.Log($"✅ ParseVersion failed: '{tag}' -> no version pattern found");
                return new System.Version("1.0.0");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ ParseVersion error: '{tag}' -> {ex.Message}");
                return new System.Version("1.0.0");
            }
        }

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdateOperation) return;
            _isUpdateOperation = true;
            try
            {
                await CheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError("CheckButton_Click", ex.Message);
            }
            finally
            {
                _isUpdateOperation = false;
            }
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdateOperation) return;
            _isUpdateOperation = true;
            try
            {
                TxtProgressStatus.Text = "Baixando atualização...";
                OverlayBusy.Visibility = Visibility.Visible;
                StatusText.Text = "⏳ Baixando atualização...";
                UpdateButton.IsEnabled = false;
                CheckButton.IsEnabled = false;

                var success = await GitHubUpdater.DownloadAndInstallUpdateAsync();

                if (success)
                {
                    StatusText.Text = "🚀 Atualização em andamento! Reiniciando...";
                    await Task.Delay(1500);
                    System.Windows.Application.Current.Shutdown();
                }
                else
                {
                    OverlayBusy.Visibility = Visibility.Collapsed;
                    StatusText.Text = "❌ Falha na atualização automática. Tente o download manual.";
                    UpdateButton.IsEnabled = true;
                    CheckButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                OverlayBusy.Visibility = Visibility.Collapsed;
                StatusText.Text = $"❌ Erro: {ex.Message}";
                UpdateButton.IsEnabled = true;
                CheckButton.IsEnabled = true;
                Logger.Log($"Erro na atualização: {ex.Message}");
            }
            finally
            {
                _isUpdateOperation = false;
            }
        }

        private void ManualDownload_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            OpenDownloadLink_Click(sender, null);
        }

        private async Task PerformUpdateAsync()
        {
            try
            {
                StatusText.Text = " Baixando atualização...";
                UpdateButton.IsEnabled = false;
                CheckButton.IsEnabled = false;
                

                var success = await DownloadAndInstallDirectAsync();
                
                if (success)
                {
                    StatusText.Text = "🚀 Atualização em andamento! Feche esta janela.";
                    UpdateButton.IsEnabled = false;
                    CheckButton.IsEnabled = false;
                    
                    // Força o fechamento da janela após 3 segundos
                    await Task.Delay(3000);
                    System.Windows.Application.Current.Shutdown();
                }
                else
                {
                    StatusText.Text = "✅ Falha na atualização";
                    UpdateButton.IsEnabled = true;
                    CheckButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"✅ Erro: {ex.Message}";
                UpdateButton.IsEnabled = true;
                CheckButton.IsEnabled = true;
                KitLugia.Core.Logger.Log($"✅ Erro na atualização: {ex.Message}");
            }
        }

        private async Task<bool> DownloadAndInstallDirectAsync()
        {
            try
            {
                if (_latestRelease?.Assets == null || _latestRelease.Assets.Length == 0)
                {
                    KitLugia.Core.Logger.Log("✅ Nenhum asset disponível");
                    return false;
                }

                // Encontra o asset correto (sempre preferir ZIP)
                var asset = Array.Find(_latestRelease.Assets, a => 
                    a.Name.Equals("KITLUGIA2.zip", StringComparison.OrdinalIgnoreCase));

                if (asset == null)
                {
                    KitLugia.Core.Logger.Log("✅ Asset KITLUGIA2.zip não encontrado");
                    return false;
                }

                KitLugia.Core.Logger.Log($"Baixando {asset.Name} ({asset.Size / 1024 / 1024}MB)");
                KitLugia.Core.Logger.Log($"🔗 URL: {asset.BrowserDownloadUrl}");

                // Download direto
                var tempDir = Path.GetTempPath();
                var updatePath = Path.Combine(tempDir, "KitLugia_Update.zip");

                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "KitLugia-Updater");
                    httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                    
                    var response = await httpClient.GetAsync(asset.BrowserDownloadUrl);
                    response.EnsureSuccessStatusCode();
                    
                    await using (var fileStream = File.Create(updatePath))
                    {
                        await response.Content.CopyToAsync(fileStream);
                    }
                }

                KitLugia.Core.Logger.Log("✅. Download concluído!");

                // Extração (sempre ZIP agora)
                var extractDir = Path.Combine(tempDir, "KitLugia_Update");
                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, true);

                ZipFile.ExtractToDirectory(updatePath, extractDir);
                File.Delete(updatePath);

                // Encontrar executável
                var exePath = FindExecutable(extractDir);
                if (string.IsNullOrEmpty(exePath))
                {
                    KitLugia.Core.Logger.Log("✅ Executável não encontrado");
                    return false;
                }

                KitLugia.Core.Logger.Log($"✅. Executável encontrado: {exePath}");


                return await PerformIntegratedUpdate(exePath);
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ Erro no download: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> PerformIntegratedUpdate(string newExePath)
        {
            try
            {
                var currentExePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var currentDir = Path.GetDirectoryName(currentExePath) ?? string.Empty;
                
                // Corrigir: se currentExePath for .dll, mudar para .exe
                if (currentExePath.EndsWith(".dll"))
                {
                    currentExePath = currentExePath.Replace(".dll", ".exe");
                }

                KitLugia.Core.Logger.Log(" Iniciando update sem processos externos...");

                // Método: Renomear próprio executável e fazer update inline
                return await PerformSelfUpdate(newExePath, currentExePath, currentDir);
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ Erro no update integrado: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> PerformSelfUpdate(string newExePath, string currentExePath, string currentDir)
        {
            try
            {
                KitLugia.Core.Logger.Log(" Iniciando auto-update sem processos externos...");

                // 1. Verificar se o destino é o próprio executável em execução
                var currentAssembly = System.Reflection.Assembly.GetEntryAssembly();
                if (currentAssembly == null)
                    currentAssembly = System.Reflection.Assembly.GetCallingAssembly();

                bool isSelfUpdate = currentAssembly.Location.ToUpper() == currentExePath.ToUpper();
                KitLugia.Core.Logger.Log($"Auto-update necessário: {isSelfUpdate}");

                if (isSelfUpdate)
                {
                    // 2. Preparar caminho para versão antiga
                    var appName = Path.GetFileNameWithoutExtension(currentExePath);
                    var appExtension = Path.GetExtension(currentExePath);
                    var archivePath = Path.Combine(currentDir, appName + "_OldVersion" + appExtension);

                    KitLugia.Core.Logger.Log($"Arquivo de backup: {archivePath}");

                    // 3. Remover versão antiga se existir
                    if (File.Exists(archivePath))
                    {
                        File.Delete(archivePath);
                        KitLugia.Core.Logger.Log("Y-'️ Versão antiga removida");
                    }

                    // 4. Renomear executável atual para versão antiga
                    File.Move(currentExePath, archivePath);
                    KitLugia.Core.Logger.Log("✅. Executável atual renomeado para versão antiga");

                    // 5. Copiar novo executável
                    File.Copy(newExePath, currentExePath, true);
                    KitLugia.Core.Logger.Log(" Novo executável copiado");

                    // Copiar DLLs e dependências
                    var sourceDir = Path.GetDirectoryName(newExePath) ?? string.Empty;
                    foreach (var dll in Directory.GetFiles(sourceDir, "*.dll"))
                    {
                        var dllName = Path.GetFileName(dll);
                        var targetDll = Path.Combine(currentDir, dllName);
                        File.Copy(dll, targetDll, true);
                    }

                    // Copiar JSONs
                    foreach (var json in Directory.GetFiles(sourceDir, "*.json"))
                    {
                        var jsonName = Path.GetFileName(json);
                        var targetJson = Path.Combine(currentDir, jsonName);
                        File.Copy(json, targetJson, true);
                    }

                    KitLugia.Core.Logger.Log("✅. Dependências copiadas");

                    // 7. Criar notificação de update completo
                    var notificationFile = Path.Combine(currentDir, "UPDATE_COMPLETE.txt");
                    var notificationContent = $@"
----------------------------------------
   KITLUGIA AUTO-UPDATE CONCLUIDO
----------------------------------------

Atualizacao concluida com sucesso!

Versao antiga: {archivePath}
Nova versao: {currentExePath}

PROXIMO PASSO:
Por favor, reinicie o KitLugia manualmente.

O Tray Icon aparecera automaticamente na bandeja do sistema.

----------------------------------------
Data: {DateTime.Now:dd/MM/yyyy HH:mm:ss}
----------------------------------------
";
                    File.WriteAllText(notificationFile, notificationContent);
                    KitLugia.Core.Logger.Log("✅. Notificação criada");

                    // 8. Abrir notificação para o usuário
                    Process.Start("notepad.exe", notificationFile);

                    // 9. Fechar aplicação atual
                    KitLugia.Core.Logger.Log("✅ Update concluído, fechando aplicação...");
                    await Task.Delay(2000);
                    System.Windows.Application.Current.Shutdown();

                    return true;
                }
                else
                {
                    // Update normal (não é o próprio executável)
                    KitLugia.Core.Logger.Log("Update normal (não é auto-update)");
                    
                    // Se o arquivo estiver em uso, criar método alternativo
                    try
                    {
                        File.Copy(newExePath, currentExePath, true);
                        KitLugia.Core.Logger.Log("✅. Novo executável copiado");
                    }
                    catch (System.IO.IOException ex) when (ex.Message.Contains("being used by another process"))
                    {
                        KitLugia.Core.Logger.Log("⚠️ Arquivo em uso, usando método alternativo...");
                        
                        // Método alternativo: criar script simples para cópia
                        var tempScript = Path.Combine(currentDir, "update_temp.cmd");
                        var scriptContent = $@"
@echo off
echo Aguardando KitLugia fechar...
timeout /t 3 /nobreak >nul
taskkill /f /im KitLugia.GUI.exe >nul 2>&1
timeout /t 2 /nobreak >nul
echo Copiando novo executavel...
copy ""{newExePath}"" ""{currentExePath}"" /Y
echo Copiando DLLs...
";
                        
                        // Copiar DLLs
                        var dllSourceDir = Path.GetDirectoryName(newExePath) ?? string.Empty;
                        foreach (var dll in Directory.GetFiles(dllSourceDir, "*.dll"))
                        {
                            var dllName = Path.GetFileName(dll);
                            var targetDll = Path.Combine(currentDir, dllName);
                            scriptContent += $"\ncopy \"{dll}\" \"{targetDll}\" /Y";
                        }
                        
                        scriptContent += $@"
echo Iniciando nova versao...
start """" ""{currentExePath}"" --tray
echo Atualizacao concluida!
del ""%~f0"" >nul 2>&1
";
                        
                        File.WriteAllText(tempScript, scriptContent);
                        KitLugia.Core.Logger.Log("✅. Script temporário criado");
                        
                        // Iniciar script
                        Process.Start("cmd.exe", $"/c \"{tempScript}\"");
                        
                        // Fechar aplicação atual
                        KitLugia.Core.Logger.Log("✅ Update iniciado, fechando aplicação...");
                        await Task.Delay(2000);
                        System.Windows.Application.Current.Shutdown();
                        
                        return true;
                    }
                    
                    // Copiar DLLs e dependências
                    var sourceDir = Path.GetDirectoryName(newExePath) ?? string.Empty;
                    foreach (var dll in Directory.GetFiles(sourceDir, "*.dll"))
                    {
                        var dllName = Path.GetFileName(dll);
                        var targetDll = Path.Combine(currentDir, dllName);
                        try
                        {
                            File.Copy(dll, targetDll, true);
                        }
                        catch (System.IO.IOException)
                        {
                            KitLugia.Core.Logger.Log($"⚠️ DLL em uso: {dllName}");
                        }
                    }

                    // Copiar JSONs
                    foreach (var json in Directory.GetFiles(sourceDir, "*.json"))
                    {
                        var jsonName = Path.GetFileName(json);
                        var targetJson = Path.Combine(currentDir, jsonName);
                        File.Copy(json, targetJson, true);
                    }

                    KitLugia.Core.Logger.Log("✅. Update normal concluído");
                    return true;
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ Erro no auto-update: {ex.Message}");
                return false;
            }
        }

        private async Task BackupCurrentVersion(string currentDir, string backupDir)
        {
            try
            {
                KitLugia.Core.Logger.Log("Fazendo backup da versão atual...");

                await Task.Run(() =>
                {
                    // Backup executável principal
                    var currentExe = Path.Combine(currentDir, "KitLugia.GUI.exe");
                    if (File.Exists(currentExe))
                    {
                        File.Copy(currentExe, Path.Combine(backupDir, "KitLugia.GUI.exe"), true);
                    }

                    // Backup DLLs principais
                    foreach (var dll in Directory.GetFiles(currentDir, "KitLugia*.dll"))
                    {
                        var dllName = Path.GetFileName(dll);
                        File.Copy(dll, Path.Combine(backupDir, dllName), true);
                    }

                // Backup arquivos de configuração
                    var configFiles = new[] { "appsettings.json", "settings.json", "*.config" };
                    foreach (var pattern in configFiles)
                    {
                        foreach (var file in Directory.GetFiles(currentDir, pattern))
                        {
                            var fileName = Path.GetFileName(file);
                            File.Copy(file, Path.Combine(backupDir, fileName), true);
                        }
                    }

                    // Backup pastas externas (external, clover, etc.)
                    var externalFolders = new[] { "external", "clover", "tools", "resources" };
                    foreach (var folder in externalFolders)
                    {
                        var sourceFolder = Path.Combine(currentDir, folder);
                        if (Directory.Exists(sourceFolder))
                        {
                            var targetFolder = Path.Combine(backupDir, folder);
                            CopyDirectory(sourceFolder, targetFolder, true);
                        }
                    }

                    KitLugia.Core.Logger.Log("✅. Backup concluído");
                });
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ Erro no backup: {ex.Message}");
            }
        }

        private async Task ExtractNewVersion(string newExePath, string updateDir)
        {
            try
            {
                KitLugia.Core.Logger.Log("Extraindo nova versão...");

                await Task.Run(() =>
                {
                    var sourceDir = Path.GetDirectoryName(newExePath) ?? string.Empty;

                    // Copiar executável principal
                    var newExeName = Path.GetFileName(newExePath);
                    File.Copy(newExePath, Path.Combine(updateDir, newExeName), true);

                    // Copiar DLLs
                    foreach (var dll in Directory.GetFiles(sourceDir, "*.dll"))
                    {
                        var dllName = Path.GetFileName(dll);
                        File.Copy(dll, Path.Combine(updateDir, dllName), true);
                    }

                // Copiar arquivos de configuração
                    foreach (var json in Directory.GetFiles(sourceDir, "*.json"))
                    {
                        var jsonName = Path.GetFileName(json);
                        File.Copy(json, Path.Combine(updateDir, jsonName), true);
                    }

                    KitLugia.Core.Logger.Log("✅. Nova versão extraída");
                });
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ Erro na extração: {ex.Message}");
            }
        }

        private async Task<string> CreateBootstrapper(string bootstrapDir, string manifestFile)
        {
            try
            {
                KitLugia.Core.Logger.Log("Criando bootstrapper API nativa...");

                // Método: Usar apenas APIs nativas do Windows
                var currentExePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (currentExePath.EndsWith(".dll"))
                {
                    currentExePath = currentExePath.Replace(".dll", ".exe");
                }

                // Criar arquivo de configuração simples
                var configFile = Path.Combine(bootstrapDir, "update.conf");
                var configContent = $@"[UPDATE]
MANIFEST={manifestFile}
EXECUTABLE={currentExePath}
ARGUMENTS=--tray
TIMESTAMP={DateTime.Now:yyyyMMddHHmmss}
";
                File.WriteAllText(configFile, configContent);

                KitLugia.Core.Logger.Log("✅. Configuração criada");

                // Método 1: Usar Windows Task Scheduler API nativa
                var taskFile = Path.Combine(bootstrapDir, "create_task.xml");
                var taskXml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Date>{DateTime.Now:yyyy-MM-dd}T{DateTime.Now:HH:mm:ss}</Date>
    <Author>KitLugia</Author>
    <Description>KitLugia Auto-Updater</Description>
  </RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <StartBoundary>{DateTime.Now.AddSeconds(5):yyyy-MM-dd}T{DateTime.Now.AddSeconds(5):HH:mm:ss}</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>S-1-5-21-0000000000-0000000000-0000000000-1001</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>true</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT72H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>cmd.exe</Command>
      <Arguments>/c ""echo Aguardando...&amp; timeout /t 3 /nobreak &gt;nul &amp; taskkill /f /im KitLugia.GUI.exe 2&gt;nul &amp; timeout /t 2 /nobreak &gt;nul &amp; robocopy ""{Path.Combine(bootstrapDir, "update")}"" ""{Path.GetDirectoryName(currentExePath)}"" /E /Y &amp; rd /s /q ""{Path.Combine(bootstrapDir, "update")}"" 2&gt;nul &amp; rd /s /q ""{Path.Combine(bootstrapDir, "backup")}"" 2&gt;nul &amp; del ""{manifestFile}"" 2&gt;nul &amp; start """" ""{currentExePath}"" --tray &amp; del ""%~f0""""</Arguments>
    </Exec>
  </Actions>
</Task>";

                File.WriteAllText(taskFile, taskXml);

                KitLugia.Core.Logger.Log("✅. XML da tarefa criado");

                // Método 2: Criar executável minimalista usando C# inline
                var minimalExe = Path.Combine(bootstrapDir, "updater.cmd");
                await CreateMinimalUpdater(minimalExe, manifestFile, currentExePath);

                KitLugia.Core.Logger.Log("✅. Script CMD minimalista criado");

                return minimalExe;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ Erro no bootstrapper API: {ex.Message}");
                
                // Plano C: Método ultra-simples
                return await CreateUltraSimpleBootstrapper(bootstrapDir, manifestFile);
            }
        }

        private async Task CreateMinimalUpdater(string cmdPath, string manifestFile, string currentExePath)
        {
            try
            {
                // Criar um arquivo .cmd válido
                var manifestDir = Path.GetDirectoryName(manifestFile) ?? string.Empty;
                var currentDir = Path.GetDirectoryName(currentExePath) ?? string.Empty;
                
                var cmdContent = $@"@echo off
title KitLugia Updater
echo Aguardando KitLugia fechar...
timeout /t 3 /nobreak >nul
taskkill /f /im KitLugia.GUI.exe >nul 2>&1
timeout /t 2 /nobreak >nul
echo Atualizando arquivos...
robocopy ""{Path.Combine(manifestDir, "update")}"" ""{currentDir}"" /E /Y >nul 2>&1
echo Limpando arquivos temporarios...
rd /s /q ""{Path.Combine(manifestDir, "update")}"" >nul 2>&1
rd /s /q ""{Path.Combine(manifestDir, "backup")}"" >nul 2>&1
del ""{manifestFile}"" >nul 2>&1
echo Iniciando nova versao...
start """" ""{currentExePath}"" --tray
echo Atualizacao concluida!
timeout /t 2 /nobreak >nul
del ""%~f0"" >nul 2>&1";
                
                await File.WriteAllTextAsync(cmdPath, cmdContent);
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ Erro ao criar script CMD: {ex.Message}");
            }
        }

        private byte[] CreateMinimalPEExecutable(string manifestFile, string currentExePath)
        {
            // Método não usado mais - mantido para compatibilidade
            var manifestDir = Path.GetDirectoryName(manifestFile) ?? string.Empty;
            var currentDir = Path.GetDirectoryName(currentExePath) ?? string.Empty;
            
            var cmdContent = $@"@echo off
title KitLugia Updater
echo Aguardando KitLugia fechar...
timeout /t 3 /nobreak >nul
taskkill /f /im KitLugia.GUI.exe >nul 2>&1
timeout /t 2 /nobreak >nul
echo Atualizando arquivos...
robocopy ""{Path.Combine(manifestDir, "update")}"" ""{currentDir}"" /E /Y >nul 2>&1
echo Limpando arquivos temporarios...
rd /s /q ""{Path.Combine(manifestDir, "update")}"" >nul 2>&1
rd /s /q ""{Path.Combine(manifestDir, "backup")}"" >nul 2>&1
del ""{manifestFile}"" >nul 2>&1
echo Iniciando nova versao...
start """" ""{currentExePath}"" --tray
echo Atualizacao concluida!
timeout /t 2 /nobreak >nul
del ""%~f0"" >nul 2>&1";
            
            return System.Text.Encoding.UTF8.GetBytes(cmdContent);
        }

        private Task<string> CreateUltraSimpleBootstrapper(string bootstrapDir, string manifestFile)
        {
            try
            {
                KitLugia.Core.Logger.Log("Criando bootstrapper ultra-simples...");

                var currentExePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (currentExePath.EndsWith(".dll"))
                {
                    currentExePath = currentExePath.Replace(".dll", ".exe");
                }

                // Método: Arquivo .cmd simples (menos suspeito que .bat)
                var cmdFile = Path.Combine(bootstrapDir, "update.cmd");
                var cmdContent = $@"@echo off
title KitLugia Updater
echo Aguardando KitLugia fechar...
timeout /t 3 /nobreak >nul
taskkill /f /im KitLugia.GUI.exe >nul 2>&1
timeout /t 2 /nobreak >nul
echo Atualizando arquivos...
robocopy ""{Path.Combine(bootstrapDir, "update")}"" ""{Path.GetDirectoryName(currentExePath)}"" /E /Y >nul 2>&1
echo Limpando arquivos temporarios...
rd /s /q ""{Path.Combine(bootstrapDir, "update")}"" >nul 2>&1
rd /s /q ""{Path.Combine(bootstrapDir, "backup")}"" >nul 2>&1
del ""{manifestFile}"" >nul 2>&1
echo Iniciando nova versao...
start """" ""{currentExePath}"" --tray
echo Atualizacao concluida!
timeout /t 2 /nobreak >nul
del ""%~f0"" >nul 2>&1";
                File.WriteAllText(cmdFile, cmdContent);

                KitLugia.Core.Logger.Log("✅. Arquivo CMD criado");

                return Task.FromResult(cmdFile);
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ Erro no bootstrapper ultra-simples: {ex.Message}");
                return Task.FromResult(string.Empty);
            }
        }

        private void CopyDirectory(string sourceDir, string targetDir, bool recursive)
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists)
                return;

            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            foreach (var file in dir.GetFiles())
            {
                var targetFile = Path.Combine(targetDir, file.Name);
                file.CopyTo(targetFile, true);
            }

            if (recursive)
            {
                foreach (var subDir in dir.GetDirectories())
                {
                    var targetSubDir = Path.Combine(targetDir, subDir.Name);
                    CopyDirectory(subDir.FullName, targetSubDir, true);
                }
            }
        }

        private class UpdateManifest
        {
            public string Version { get; set; } = string.Empty;
            public string CurrentExe { get; set; } = string.Empty;
            public string UpdateDir { get; set; } = string.Empty;
            public string BackupDir { get; set; } = string.Empty;
            public string TargetDir { get; set; } = string.Empty;
            public string Arguments { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        private string FindExecutable(string directory)
        {
            try
            {
                var exeNames = new[] { "KITLUGIA2.exe", "KitLugia.GUI.exe", "KitLugia.exe" };
                
                foreach (var exeName in exeNames)
                {
                    var files = Directory.GetFiles(directory, exeName, SearchOption.AllDirectories);
                    if (files.Length > 0)
                    {
                        KitLugia.Core.Logger.Log($"✅. Executável encontrado: {files[0]}");
                        return files[0];
                    }
                }
                
                KitLugia.Core.Logger.Log("✅ Nenhum executável encontrado");
                return "";
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ Erro ao procurar executável: {ex.Message}");
                return "";
            }
        }

        private string CreateUpdateScript(string newExePath, string currentExePath, string extractDir)
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), "update_kitlugia.bat");
            // ✅. CORREÇÃO: Adicionar null checks para evitar CS8604
            var currentDir = Path.GetDirectoryName(currentExePath) ?? AppContext.BaseDirectory;
            var sourceDir = Path.GetDirectoryName(newExePath) ?? AppContext.BaseDirectory;
            
            // Corrigir: se currentExePath for .dll, mudar para .exe
            if (currentExePath.EndsWith(".dll"))
            {
                currentExePath = currentExePath.Replace(".dll", ".exe");
            }
            
            // ✅. CORREÇÃO: Garantir que currentDir não seja null
            var controlFile = Path.Combine(currentDir ?? AppContext.BaseDirectory, "kitlugia_update_control.txt");
            File.WriteAllText(controlFile, $"KITLUGIA_EXE={currentExePath}\nKITLUGIA_DIR={currentDir}\nKITLUGIA_ARGS=--tray");
            
            var script = $@"
@echo off
title KitLugia Auto-Updater v2.5.0
echo.
echo ========================================
echo    KitLugia Auto-Updater v2.5.0
echo ========================================
echo.
echo [INFO] Executavel atual: {currentExePath}
echo [INFO] Novo executavel: {newExePath}
echo [INFO] Diretorio de extracao: {extractDir}
echo [INFO] Diretorio atual: {currentDir}
echo [INFO] Diretorio origem: {sourceDir}
echo [INFO] Arquivo de controle: {controlFile}
echo.

echo [1/6] Aguardando KitLugia fechar...
timeout /t 2 /nobreak >nul

echo [2/6] Forcando fechamento do processo original...
taskkill /f /im KitLugia.GUI.exe 2>nul
taskkill /f /im KitLugia.exe 2>nul

echo [3/6] Aguardando mais 2 segundos...
timeout /t 2 /nobreak >nul

echo [4/6] Copiando novo executavel...
copy ""{newExePath}"" ""{currentExePath}"" /Y
if errorlevel 1 (
    echo [ERRO] Falha ao copiar executavel
    pause
    exit /b 1
)

echo [5/6] Copiando DLLs e dependencias...
xcopy ""{sourceDir}\*.dll"" ""{currentDir}"" /Y /I /Q
xcopy ""{sourceDir}\*.json"" ""{currentDir}"" /Y /I /Q
xcopy ""{sourceDir}\*.config"" ""{currentDir}"" /Y /I /Q
xcopy ""{sourceDir}\*.pdb"" ""{currentDir}"" /Y /I /Q
xcopy ""{sourceDir}\*.xml"" ""{currentDir}"" /Y /I /Q

echo [6/6] Iniciando nova versao...
echo [INFO] Lendo arquivo de controle...
set /p KITLUGIA_EXE=<{controlFile}
for /f ""tokens=2 delims=="" %%a in (""findstr /B ""KITLUGIA_EXE="" {controlFile}"") do set KITLUGIA_PATH=%%a
for /f ""tokens=2 delims=="" %%a in (""findstr /B ""KITLUGIA_ARGS="" {controlFile}"") do set KITLUGIA_ARGS=%%a

echo [INFO] Executavel encontrado: %KITLUGIA_PATH%
echo [INFO] Argumentos: %KITLUGIA_ARGS%
echo [INFO] Iniciando KitLugia atualizado...
cd /d ""{currentDir}""
%KITLUGIA_PATH% %KITLUGIA_ARGS%

echo [INFO] Aguardando 3 segundos para verificar inicializacao...
timeout /t 3 /nobreak >nul

tasklist /fi ""imagename eq KitLugia.GUI.exe"" 2>nul | find /i ""KitLugia.GUI.exe"" >nul
if errorlevel 1 (
    echo [AVISO] KitLugia.GUI.exe nao encontrado em execucao
    echo [INFO] Tentando iniciar novamente...
    %KITLUGIA_PATH% %KITLUGIA_ARGS%
    timeout /t 2 /nobreak >nul
) else (
    echo [SUCESSO] KitLugia.GUI.exe esta em execucao
)

echo [INFO] Aguardando 5 segundos para a nova versao inicializar completamente...
timeout /t 5 /nobreak >nul

echo [INFO] Limpando arquivos temporários...
del ""{controlFile}"" 2>nul
rd /s /q ""{extractDir}""
del ""%~f0""

echo ========================================
echo    Atualizacao concluida com sucesso!
echo ========================================
echo [INFO] A nova versao ira verificar e corrigir os metodos de inicializacao automaticamente.
echo [INFO] Verifique se o KitLugia esta funcionando corretamente com Tray Icon ativo.
timeout /t 3 /nobreak >nul
";

            File.WriteAllText(scriptPath, script);
            return scriptPath;
        }
    }
}
