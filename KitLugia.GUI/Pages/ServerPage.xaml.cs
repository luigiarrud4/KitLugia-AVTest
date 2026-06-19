using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;

using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;

namespace KitLugia.GUI.Pages
{
    /// <summary>
    /// Servidor Minecraft Público Simplificado
    /// 
    /// Funcionamento CORRETO:
    /// 1. Usuário abre mundo LAN no Minecraft → Minecraft gera porta (ex: 54321)
    /// 2. Usuário clica em "🔍 Detectar" → KitLugia detecta porta automaticamente
    /// 3. Usuário clica "▶ Iniciar" → KitLugia obtém IP público
    /// 4. KitLugia gera: IP_PUBLICO:PORTA_MINECRAFT
    /// 5. Amigos conectam diretamente usando esse endereço
    /// </summary>
    public partial class ServerPage : Page
    {
        private const int MaxLogLines = 100;
        private bool _isRunning = false;
        private string _currentEndpoint = "";
        private int _minecraftPort = 0;
        private readonly List<string> _logLines = new();
        private Process? _tunnelProcess;
        private PlayitTunnelAdapter? _playitAdapter;

        public ServerPage()
        {
            InitializeComponent();
            Log("🌐 Servidor Minecraft pronto. Abra o mundo e clique em Detectar.");
        }

        // ─── Detectar Porta do Minecraft ─────────────────────────────────────

        private async void BtnDetect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("🔍 Detectando porta do Minecraft LAN...");
                
                var ports = new List<(int Port, string ProcessName)>();

                var psi = new ProcessStartInfo
                {
                    FileName = "netstat.exe",
                    Arguments = "-ano -p TCP",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    ports = await Task.Run(() =>
                    {
                        var result = new List<(int Port, string ProcessName)>();
                        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!line.Contains("LISTENING")) continue;

                            var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length < 5) continue;

                            var addrParts = parts[1].Split(':');
                            if (addrParts.Length < 2) continue;
                            if (!int.TryParse(addrParts[^1], out int port)) continue;
                            if (port < 1024 || port > 65535) continue;
                            if (!int.TryParse(parts[4], out int pid) || pid <= 0) continue;

                            try
                            {
                                string procName = Process.GetProcessById(pid).ProcessName;
                                result.Add((port, procName));
                            }
                            catch { }
                        }
                        return result;
                    });
                }

                // Prioriza portas do Minecraft (javaw, minecraft)
                var mcPorts = ports.Where(p =>
                    p.ProcessName.IndexOf("javaw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.ProcessName.IndexOf("minecraft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.ProcessName.IndexOf("java", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                if (mcPorts.Count > 0)
                {
                    // Pega a porta mais alta (geralmente a do LAN, javaw costuma usar portas mais altas)
                    // Portas mais altas costumam ser as de LAN (ex: 51648 vs 37842)
                    var best = mcPorts.OrderByDescending(p => p.Port).First();
                    _minecraftPort = best.Port;
                    
                    if (TxtMinecraftPort != null)
                    {
                        TxtMinecraftPort.Text = best.Port.ToString();
                    }

                    Log($"🎮 Minecraft detectado! Porta: {best.Port} ({best.ProcessName})");
                    foreach (var p in mcPorts.Take(5))
                        Log($"   📌 {p.Port} → {p.ProcessName}");

                    MessageBox.Show(
                        $"🎮 Minecraft detectado!\n\nPorta detectada: {best.Port}\n\nSe não for a porta correta, verifique no Minecraft (ESC → Abrir para LAN) e clique em Detectar novamente.",
                        "Porta Detectada",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else if (ports.Count > 0)
                {
                    Log("ℹ️ Minecraft não detectado. Portas abertas:");
                    foreach (var p in ports.Take(8))
                        Log($"   {p.Port} → {p.ProcessName}");

                    MessageBox.Show(
                        $"Minecraft não foi detectado automaticamente.\n\nPortas abertas encontradas: {string.Join(", ", ports.Take(5).Select(p => p.Port))}\n\nAbra o mundo LAN no Minecraft primeiro (ESC → Abrir para LAN) e clique em Detectar novamente.",
                        "Minecraft Não Detectado",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    Log("ℹ️ Nenhuma porta encontrada. Abra o mundo LAN no Minecraft primeiro.");
                    MessageBox.Show(
                        "Nenhuma porta encontrada.\n\nAbra o mundo LAN no Minecraft:\n1. Pause o jogo (ESC)\n2. Clique em 'Abrir para LAN'\n3. Anote a porta gerada\n4. Clique em 'Detectar' novamente",
                        "Nenhuma Porta",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Erro ao detectar: {ex.Message}");
            }
        }

        // ─── Iniciar/Parar Servidor ─────────────────────────────────────────

        private async void BtnToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                // Parar
                _isRunning = false;
                _currentEndpoint = "";
                _minecraftPort = 0;
                StopTunnel();
                
                if (BtnToggle != null) BtnToggle.Content = "▶ Iniciar";
                if (StatusText != null) StatusText.Text = "Parado";
                if (StatusDetail != null) StatusDetail.Text = "Abra o Minecraft e clique em Detectar";
                if (StatusIcon != null) StatusIcon.Text = "🌐";
                if (TxtEndpoint != null) TxtEndpoint.Text = "Aguardando detecção...";
                if (TxtMinecraftPort != null) TxtMinecraftPort.Text = "";
                
                Log("⏹️ Servidor parado.");
            }
            else
            {
                // Iniciar
                // Verificar se o usuário digitou uma porta manualmente ou se foi detectada
                if (string.IsNullOrEmpty(TxtMinecraftPort?.Text) || !int.TryParse(TxtMinecraftPort.Text, out int manualPort) || manualPort < 1 || manualPort > 65535)
                {
                    Log("❌ Digite uma porta válida ou clique em Detectar!");
                    MessageBox.Show(
                        "Por favor, digite uma porta válida (1-65535) ou clique em '🔍 Detectar' para encontrar automaticamente.",
                        "Porta Inválida",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                
                // Usar a porta digitada pelo usuário
                _minecraftPort = manualPort;

                _isRunning = true;
                
                if (BtnToggle != null) BtnToggle.Content = "⏹ Parar";
                if (StatusText != null) StatusText.Text = "Iniciando...";
                if (StatusDetail != null) StatusDetail.Text = "Criando tunnel automático...";
                if (StatusIcon != null) StatusIcon.Text = "⚡";
                
                Log("🚀 Iniciando servidor com tunnel automático...");

                try
                {
                    // Criar tunnel automático primeiro
                    await CreateTunnelAsync();
                    
                    // Testar automaticamente se o servidor está funcionando
                    Log("🧪 Testando automaticamente se o servidor está acessível...");
                    bool autoTest = await TestConnectionAsync("localhost", _minecraftPort, 3000);
                    
                    if (autoTest)
                    {
                        Log("✅ Teste automático: Servidor respondendo localmente!");
                    }
                    else
                    {
                        Log("⚠️ Teste automático: Servidor não está respondendo localmente.");
                        Log("   Verifique se o Minecraft está com o mundo aberto para LAN.");
                    }
                    
                    MessageBox.Show(
                        $"✅ Servidor iniciado com sucesso!\n\nEndereço para seus amigos:\n{_currentEndpoint}\n\nStatus: {(autoTest ? "✅ Funcionando" : "⚠️ Verificar configuração")}\n\nCopie e envie para que eles possam conectar.",
                        "Servidor Ativo",
                        MessageBoxButton.OK,
                        autoTest ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
                catch (Exception ex)
                {
                    Log($"❌ Erro ao iniciar: {ex.Message}");
                    
                    _isRunning = false;
                    if (BtnToggle != null) BtnToggle.Content = "▶ Iniciar";
                    if (StatusText != null) StatusText.Text = "Erro";
                    if (StatusDetail != null) StatusDetail.Text = ex.Message;
                    if (StatusIcon != null) StatusIcon.Text = "❌";
                    
                    MessageBox.Show(
                        $"Erro ao iniciar servidor:\n{ex.Message}",
                        "Erro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private async Task CreateTunnelAsync()
        {
            try
            {
                Log("🚀 Iniciando túnel Playit.gg (foco em um método só)...");
                
                // Usar apenas Playit.gg - sem fallbacks
                var playitAdapter = new PlayitTunnelAdapter();
                playitAdapter.OnLogMessage += (msg) => Log(msg);
                playitAdapter.OnTunnelCreated += (url) => {
                    _currentEndpoint = url;
                    if (TxtEndpoint != null) TxtEndpoint.Text = _currentEndpoint;
                };
                
                // Iniciar túnel Playit.gg
                var tunnelResult = await playitAdapter.CreatePlayitTunnelAsync(_minecraftPort);
                if (!tunnelResult.Success)
                {
                    Log($"❌ Playit.gg falhou: {tunnelResult.Message}");
                    throw new Exception($"Falha ao criar túnel Playit.gg: {tunnelResult.Message}");
                }
                
                // Configurar endpoint com URL do Playit.gg
                _currentEndpoint = tunnelResult.PublicUrl;
                
                if (TxtEndpoint != null) TxtEndpoint.Text = _currentEndpoint;
                if (StatusText != null) StatusText.Text = "🟢 Ativo";
                if (StatusDetail != null) StatusDetail.Text = $"Playit.gg";
                if (StatusIcon != null) StatusIcon.Text = "✅";
                
                Log($"✅ Túnel Playit.gg criado!");
                Log($"🌐 URL Pública: {tunnelResult.PublicUrl}");
                Log($"🎮 Otimizado para jogos!");
                Log($"📋 Endereço para amigos: {_currentEndpoint}");
                Log($"🎮 Seus amigos conectarão usando: {_currentEndpoint}");
                Log($"💡 Foco em Playit.gg - método único!");
                
                // Guardar referência para cleanup
                _playitAdapter = playitAdapter;
            }
            catch (Exception ex)
            {
                Log($"❌ Erro ao criar túnel Playit.gg: {ex.Message}");
                throw new Exception($"Falha ao configurar servidor: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Obtém IP do WARP (se disponível)
        /// </summary>
        private async Task<string?> GetWarpIpAsync()
        {
            try
            {
                // Tenta obter IP do WARP via API
                using var client = new HttpClient();
                var response = await client.GetAsync("https://cloudflare.com/cdn-cgi/trace");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var ipMatch = System.Text.RegularExpressions.Regex.Match(content, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
                    if (ipMatch.Success)
                    {
                        return ipMatch.Groups[1].Value;
                    }
                }
            }
            catch { }
            
            return null;
        }

        private async Task CreateDirectIpEndpointAsync()
        {
            var publicIp = await GetPublicIPAsync();
            if (!string.IsNullOrEmpty(publicIp))
            {
                _currentEndpoint = $"{publicIp}:{_minecraftPort}";
                
                if (TxtEndpoint != null) TxtEndpoint.Text = _currentEndpoint;
                if (StatusText != null) StatusText.Text = "🟢 Ativo";
                if (StatusDetail != null) StatusDetail.Text = $"IP Público: {publicIp}";
                if (StatusIcon != null) StatusIcon.Text = "✅";
                
                Log($"✅ Servidor ativo: {_currentEndpoint}");
                Log($"📋 Envie para seus amigos: {_currentEndpoint}");
                Log($"🎮 Seus amigos conectarão usando: {_currentEndpoint}");
                Log($"💡 Dica: Seus amigos precisam abrir porta no roteador ou usar VPN");
            }
            else
            {
                throw new Exception("Não foi possível obter IP público.");
            }
        }

        // ─── Tunnel ───────────────────────────────────────────────

        private async void BtnTunnel_Click(object sender, RoutedEventArgs e)
        {
            if (_minecraftPort == 0)
            {
                Log("❌ Detecte a porta do Minecraft primeiro!");
                MessageBox.Show(
                    "Por favor, clique em '🔍 Detectar' para encontrar a porta do Minecraft LAN antes de criar o tunnel.",
                    "Porta Não Detectada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                Log("🚀 Iniciando tunnel automático...");
                
                // Parar tunnel existente
                StopTunnel();

                // Verificar se localtunnel está instalado
                string? localtunnelPath = await FindLocaltunnelPathAsync();
                if (string.IsNullOrEmpty(localtunnelPath))
                {
                    Log("❌ localtunnel não encontrado. Instalando automaticamente...");
                    await InstallLocaltunnelAsync();
                    localtunnelPath = await FindLocaltunnelPathAsync();
                    
                    if (string.IsNullOrEmpty(localtunnelPath))
                    {
                        Log("❌ Falha ao instalar localtunnel.");
                        MessageBox.Show(
                            "Não foi possível instalar o localtunnel automaticamente.\n\nPor favor, instale manualmente:\nnpm install -g localtunnel",
                            "Erro de Instalação",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }
                }

                // Gerar subdomínio aleatório para evitar conflitos
                string subdomain = $"kitlugia-{Guid.NewGuid():N}";
                
                // Iniciar tunnel
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c lt --port {_minecraftPort} --subdomain {subdomain}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _tunnelProcess = Process.Start(startInfo);
                
                if (_tunnelProcess != null)
                {
                    Log($"🚀 Tunnel iniciado na porta {_minecraftPort}");
                    
                    // Ler output para encontrar URL
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string output = await _tunnelProcess.StandardOutput.ReadToEndAsync();
                            string error = await _tunnelProcess.StandardError.ReadToEndAsync();
                            
                            // Procurar URL no output
                            var urlMatch = System.Text.RegularExpressions.Regex.Match(output, @"https?://([a-zA-Z0-9.-]+\.loca\.lt)");
                            if (urlMatch.Success)
                            {
                                string tunnelHost = urlMatch.Groups[1].Value; // Pega só o host sem https://
                                _currentEndpoint = $"{tunnelHost}:{_minecraftPort}";
                                
                                Dispatcher.Invoke(() =>
                                {
                                    if (TxtEndpoint != null) TxtEndpoint.Text = _currentEndpoint;
                                    Log($"✅ Tunnel criado: {_currentEndpoint}");
                                    Log($"📋 Envie para seus amigos: {_currentEndpoint}");
                                    Log($"🎮 Seus amigos conectarão usando: {_currentEndpoint}");
                                });
                            }
                            else
                            {
                                Dispatcher.Invoke(() => Log("⚠️ Aguardando URL do tunnel..."));
                            }
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() => Log($"❌ Erro ao ler tunnel: {ex.Message}"));
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Erro ao iniciar tunnel: {ex.Message}");
                MessageBox.Show(
                    $"Erro ao iniciar tunnel:\n{ex.Message}",
                    "Erro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async Task<string?> FindLocaltunnelPathAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c npm root -g",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    
                    if (output.Contains("localtunnel"))
                    {
                        return "localtunnel";
                    }
                }
            }
            catch { }
            
            return null;
        }

        private async Task InstallLocaltunnelAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c npm install -g localtunnel",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync();
                    string error = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    
                    Log($"📦 Instalação: {output}");
                    if (!string.IsNullOrEmpty(error))
                    {
                        Log($"⚠️ Avisos: {error}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Erro na instalação: {ex.Message}");
            }
        }

        private void StopTunnel()
        {
            try
            {
                if (_tunnelProcess != null && !_tunnelProcess.HasExited)
                {
                    _tunnelProcess.Kill();
                    _tunnelProcess.Dispose();
                    _tunnelProcess = null;
                    Log("⏹️ Tunnel parado.");
                }
            }
            catch { }
        }

        // ─── Testar Conexão ───────────────────────────────────────────────

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning || _minecraftPort == 0)
            {
                Log("❌ Inicie o servidor primeiro para testar!");
                MessageBox.Show(
                    "Por favor, inicie o servidor primeiro clicando em '▶ Iniciar'.",
                    "Servidor Não Iniciado",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                Log("🧪 Testando conexão local...");
                
                // Teste 1: Conexão local (localhost)
                bool localTest = await TestConnectionAsync("localhost", _minecraftPort, 3000);
                
                // Teste 2: Conexão IP público
                var publicIp = await GetPublicIPAsync();
                bool publicTest = false;
                if (!string.IsNullOrEmpty(publicIp))
                {
                    publicTest = await TestConnectionAsync(publicIp, _minecraftPort, 5000);
                }

                Log($"🧪 Resultados:");
                Log($"   🏠 Local (localhost:{_minecraftPort}): {(localTest ? "✅ Acessível" : "❌ Inacessível")}");
                if (!string.IsNullOrEmpty(publicIp))
                {
                    Log($"   🌍 Público ({publicIp}:{_minecraftPort}): {(publicTest ? "✅ Acessível" : "❌ Inacessível")}");
                }

                if (localTest)
                {
                    Log("✅ Servidor está funcionando! Seus amigos devem conseguir conectar.");
                    MessageBox.Show(
                        "✅ Teste concluído!\n\nServidor está funcionando localmente.\n\nSeus amigos devem conseguir conectar usando o endereço fornecido.\n\nDica: Se eles não conseguirem, verifique se o firewall está bloqueando a porta.",
                        "Teste Bem-Sucedido",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    Log("❌ Servidor não está respondendo localmente.");
                    MessageBox.Show(
                        "❌ Teste falhou!\n\nO servidor não está respondendo localmente.\n\nVerifique:\n1. Minecraft está com mundo aberto para LAN?\n2. Porta está correta?\n3. Firewall não está bloqueando?",
                        "Teste Falhou",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Erro no teste: {ex.Message}");
                MessageBox.Show(
                    $"Erro durante o teste:\n{ex.Message}",
                    "Erro de Teste",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static async Task<bool> TestConnectionAsync(string host, int port, int timeoutMs)
        {
            try
            {
                using var tcpClient = new TcpClient();
                var connectTask = tcpClient.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(timeoutMs);
                var completed = await Task.WhenAny(connectTask, timeoutTask);
                return completed == connectTask && tcpClient.Connected;
            }
            catch
            {
                return false;
            }
        }

        // ─── Copiar Endereço ────────────────────────────────────────────────

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentEndpoint))
            {
                Log("❌ Nenhum endereço para copiar.");
                return;
            }

            Clipboard.SetText(_currentEndpoint);
            Log($"📋 Endereço copiado: {_currentEndpoint}");

            var orig = BtnCopy.Content;
            if (BtnCopy != null)
            {
                BtnCopy.Content = "✅ Copiado!";
                _ = Task.Delay(1500).ContinueWith(_ =>
                    Dispatcher.Invoke(() => BtnCopy.Content = orig));
            }
        }

        // ─── Log ─────────────────────────────────────────────────────────────

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            if (_logLines.Count == 0) return;
            Clipboard.SetText(string.Join(Environment.NewLine, _logLines));
            Log("✅ Log copiado.");
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            if (TxtLog != null) TxtLog.Text = "🗑️ Log limpo.";
            if (TxtLogCount != null) TxtLogCount.Text = "0";
        }

        private void Log(string message)
        {
            try
            {
                string ts = DateTime.Now.ToString("HH:mm:ss");
                string entry = $"[{ts}] {message}";

                lock (_logLines)
                {
                    _logLines.Add(entry);
                    if (_logLines.Count > MaxLogLines)
                        _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
                }

                Dispatcher.Invoke(() =>
                {
                    if (TxtLog == null) return;
                    TxtLog.Text += "\n" + entry;
                    if (TxtLogCount != null) TxtLogCount.Text = _logLines.Count.ToString();
                    LogScroller?.ScrollToEnd();
                });
            }
            catch { }
        }

        // ─── Utilitários de Rede ─────────────────────────────────────────────

        /// <summary>
        /// Obtém o IP público da máquina consultando serviços externos.
        /// Tenta múltiplos serviços em paralelo para maior velocidade.
        /// </summary>
        private static async Task<string?> GetPublicIPAsync()
        {
            var services = new[]
            {
                "https://api.ipify.org",
                "https://ipinfo.io/ip",
                "https://checkip.amazonaws.com",
                "https://icanhazip.com",
                "https://ipecho.net/plain"
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // Tenta os serviços em sequência (mais confiável que paralelo para evitar rate limiting)
            foreach (var url in services)
            {
                try
                {
                    if (cts.Token.IsCancellationRequested) break;
                    string ip = (await client.GetStringAsync(url, cts.Token)).Trim();
                    if (IPAddress.TryParse(ip, out _) && ip != "0.0.0.0" && ip != "127.0.0.1")
                        return ip;
                }
                catch { }
            }

            return null;
        }

        // ─── Cleanup ────────────────────────────────────────────────────────

        public void Cleanup()
        {
            StopTunnel();
            
            // Parar Playit.gg se estiver ativo
            if (_playitAdapter != null)
            {
                _playitAdapter.StopTunnel();
                _playitAdapter.Dispose();
                _playitAdapter = null;
            }
            
            _currentEndpoint = "";
            _minecraftPort = 0;
            this.Unloaded -= Page_Unloaded;
            this.DataContext = null;
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e) => Cleanup();
    }
}
