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

                var assemblyPath = assembly.Location;
                if (string.IsNullOrEmpty(assemblyPath))
                    assemblyPath = Environment.ProcessPath ?? AppContext.BaseDirectory;

                var buildDate = System.IO.File.GetLastWriteTime(assemblyPath);
                
                // ✅. Adicionar timeout de 10 segundos para evitar congelamento
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                
                var versionInfo = await Task.Run(async () => 
                {
                    return await KitLugia.Core.SmartVersionDetector.GetVersionInfoAsync(buildDate);
                }, cts.Token);
                
                var localBuildDate = DateTimeOffset.Now; // Data/hora atual com timezone do usuário
                var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";


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
                var assemblyPath = assembly.Location;
                if (string.IsNullOrEmpty(assemblyPath))
                    assemblyPath = Environment.ProcessPath ?? AppContext.BaseDirectory.TrimEnd('\\') + "\\KitLugia.GUI.exe";
                var buildDate = System.IO.File.GetLastWriteTime(assemblyPath);
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

                // Fluxo: download ZIP + updater visível + shutdown
                var success = await DownloadAndLaunchUpdaterAsync();

                if (success)
                {
                    StatusText.Text = "🚀 Atualização em andamento! O updater abrirá uma janela.";
                    await Task.Delay(2000);
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

        private async Task<bool> DownloadAndLaunchUpdaterAsync()
        {
            try
            {
                if (_latestRelease?.Assets == null || _latestRelease.Assets.Length == 0)
                {
                    KitLugia.Core.Logger.Log("❌ Nenhum asset disponível");
                    return false;
                }

                var asset = Array.Find(_latestRelease.Assets, a =>
                    a.Name.Equals("KITLUGIA2.zip", StringComparison.OrdinalIgnoreCase));
                if (asset == null)
                {
                    KitLugia.Core.Logger.Log("❌ Asset KITLUGIA2.zip não encontrado");
                    return false;
                }

                KitLugia.Core.Logger.Log($"Baixando {asset.Name} ({asset.Size / 1024 / 1024}MB)");

                var tempDir = Path.GetTempPath();
                var zipPath = Path.Combine(tempDir, "KitLugia_Update.zip");

                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "KitLugia-Updater");
                    httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                    var response = await httpClient.GetAsync(asset.BrowserDownloadUrl);
                    response.EnsureSuccessStatusCode();
                    await using (var fileStream = File.Create(zipPath))
                        await response.Content.CopyToAsync(fileStream);
                }

                KitLugia.Core.Logger.Log("✅ Download concluído!");

                // Baixar hash
                string expectedHash = "";
                try
                {
                    using var hc = new HttpClient();
                    expectedHash = (await hc.GetStringAsync(asset.BrowserDownloadUrl.Replace(".zip", ".zip.sha256"))).Trim();
                }
                catch { }

                // Encontrar o updater
                string currentDir = AppDomain.CurrentDomain.BaseDirectory;
                string updaterPath = Path.Combine(currentDir, "KitLugia.Updater.exe");
                if (!File.Exists(updaterPath))
                {
                    try
                    {
                        var asm = typeof(KitLugia.Core.GitHubUpdater).Assembly;
                        using var stream = asm.GetManifestResourceStream("KitLugia.Core.Resources.KitLugia.Updater.exe");
                        if (stream != null)
                        {
                            updaterPath = Path.Combine(Path.GetTempPath(), "KitLugia.Updater.exe");
                            using var file = File.Create(updaterPath);
                            stream.CopyTo(file);
                        }
                    }
                    catch (Exception ex) { KitLugia.Core.Logger.Log($"⚠️ Erro ao extrair updater: {ex.Message}"); }
                }

                if (!File.Exists(updaterPath))
                {
                    KitLugia.Core.Logger.Log("❌ KitLugia.Updater.exe não encontrado!");
                    File.Delete(zipPath);
                    return false;
                }

                int currentPid = Process.GetCurrentProcess().Id;
                string currentExePath = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(currentExePath))
                    currentExePath = Path.Combine(currentDir, "KitLugia.GUI.exe");

                KitLugia.Core.Logger.Log($"🚀 Iniciando KitLugia.Updater.exe (visível)...");

                var psi = new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = $"\"{zipPath}\" {currentPid} \"{currentExePath}\" \"{expectedHash}\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal,
                };
                Process.Start(psi);

                return true;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"❌ Erro no download/updater: {ex.Message}");
                return false;
            }
        }
    }
}
