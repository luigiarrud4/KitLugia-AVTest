// ═══════════════════════════════════════════════════════════════════════
// TEMPLATE DE PÁGINA — KitLugia GUI
// ═══════════════════════════════════════════════════════════════════════
//
// INSTRUÇÕES:
// 1. Copie este arquivo e renomeie para SuaPagina.xaml.cs
// 2. Copie o XAML correspondente (_PageTemplate.xaml) e renomeie
// 3. Registre o PageType no MainWindow.xaml.cs (switch NavigateToPage)
// 4. Adicione botão de navegação no DashboardPage.xaml se aplicável
//
// PADRÕES OBRIGATÓRIOS:
// - Encoding: UTF-8 com BOM (salvar como UTF-8 no Visual Studio)
// - Cleanup(): público, chamado via reflection pelo MainWindow.CleanupAndNavigate
// - Unloaded handler: sempre registrar no construtor, sempre chamar Cleanup()
// - CTS: se usar CancellationTokenSource, Cancel + Dispose + null no Cleanup
// - Timers: se usar DispatcherTimer, Stop + null no Cleanup
// - Eventos: unsubscriver todos no Cleanup (Loaded, Unloaded, handlers customizados)
// - DataContext: sempre setar null no Cleanup (libera bindings e reduce memory)
// - using declarations: usar quando possível (RegistryKey, Process, etc.)
// - Try/catch em operações de registro/IO: nunca deixar exceções escaparem silenciosamente
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
// using KitLugia.GUI.Services; // descomentar se usar TrayIconService/TweakRegistry

namespace KitLugia.GUI.Pages
{
    public partial class SuaPagina : Page
    {
        // ── State ──────────────────────────────────────────────────────
        private bool _isLoading;
        private CancellationTokenSource? _cts;
        // private DispatcherTimer? _timer; // descomentar se usar timer

        // ── Constructor ────────────────────────────────────────────────
        public SuaPagina()
        {
            InitializeComponent();

            // SEMPRE registrar Unloaded para Cleanup
            this.Unloaded += SuaPagina_Unloaded;

            // Registrar Loaded se precisar carregar dados ao entrar na página
            this.Loaded += SuaPagina_Loaded;
        }

        // ── Lifecycle ──────────────────────────────────────────────────

        private async void SuaPagina_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadDataAsync();
        }

        private void SuaPagina_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        /// <summary>
        /// Cleanup chamado via reflection pelo MainWindow.CleanupAndNavigate.
        /// DEVE ser público. Ordem: cancelar recursos → unsubscribir → null DataContext.
        /// </summary>
        public void Cleanup()
        {
            // 1. Cancelar CTS (impede tasks em background de acessarem UI)
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            // 2. Parar timers
            // _timer?.Stop();
            // _timer = null;

            // 3. Unsubscribir event handlers (evita memory leaks)
            this.Loaded -= SuaPagina_Loaded;
            this.Unloaded -= SuaPagina_Unloaded;
            // Outros handlers: this.SomeControl.Click -= handler;

            // 4. Limpar dados
            // MinhaCollection?.Clear();

            // 5. Liberar DataContext (libera bindings)
            this.DataContext = null;
        }

        // ── Data Loading ───────────────────────────────────────────────

        private async Task LoadDataAsync()
        {
            if (_isLoading) return;
            _isLoading = true;

            try
            {
                _cts?.Cancel(); // cancelar loading anterior
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                // Exemplo: carregar dados em background
                var data = await Task.Run(() =>
                {
                    // Trabalho pesado aqui (registry, IO, etc.)
                    token.ThrowIfCancellationRequested();
                    return new object(); // substituir pelo dado real
                }, token);

                // Atualizar UI na thread principal
                Dispatcher.Invoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    // Atualizar bindings/coleções aqui
                });
            }
            catch (OperationCanceledException)
            {
                // Loading cancelado — silencioso
            }
            catch (Exception ex)
            {
                Logger.LogError("SuaPagina.LoadData", ex.Message);
            }
            finally
            {
                _isLoading = false;
            }
        }

        // ── Event Handlers ─────────────────────────────────────────────

        // Exemplo de toggle:
        // private async void ChkMeuToggle_Click(object sender, RoutedEventArgs e)
        // {
        //     if (_isLoading) return;
        //     _isLoading = true;
        //     try
        //     {
        //         bool targetActive = ChkMeuToggle.IsChecked == true;
        //         await Task.Run(() =>
        //         {
        //             if (targetActive) SystemTweaks.SomeApply();
        //             else SystemTweaks.SomeRevert();
        //         });
        //         UpdateLabel(StatusLabel, targetActive, "Ativo", "Inativo");
        //     }
        //     catch (Exception ex)
        //     {
        //         Logger.LogError("SuaPagina.Toggle", ex.Message);
        //     }
        //     finally
        //     {
        //         _isLoading = false;
        //     }
        // }

        // ── Helpers ────────────────────────────────────────────────────

        private void UpdateLabel(TextBlock label, bool isActive, string textActive, string textInactive)
        {
            label.Text = isActive ? textActive : textInactive;
            label.Foreground = isActive
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(108, 203, 95))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 150, 150));
        }
    }
}
