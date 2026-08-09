using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KitLugia.GUI.Logging;

// Resolve ambiguidades
using UserControl = System.Windows.Controls.UserControl;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace KitLugia.GUI.Controls
{
    public partial class GlobalConsole : UserControl
    {
        // Evento para avisar a MainWindow que o usuário quer fechar o console
        public event EventHandler? RequestClose;

        // Auto-scroll inteligente: só acompanha se o usuário estiver no rodapé.
        private bool _stickToBottom = true;
        private bool _copyAllInFlight;

        public GlobalConsole()
        {
            InitializeComponent();

            LogList.ItemsSource = ConsoleManager.Logs;

            // ScrollChanged é evento do ScrollViewer interno do template do ListBox (roteado).
            LogList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(LogList_ScrollChanged));

            // Captura Ctrl+C / Ctrl+A em QUALQUER parte do console (tunneling), mesmo
            // quando o foco está na barra de ferramentas e não no ListBox.
            PreviewKeyDown += GlobalConsole_PreviewKeyDown;

            // Sincroniza com o ConsoleManager
            ConsoleManager.OnLogAdded += OnLogAdded;

            UpdateStatusDisplay();
        }

        // Anexa ao dispatcher (barato) e bate o scroll no fim somente quando o usuário
        // estiver no rodapé — investigar logs antigos não é mais roubado pelo auto-scroll.
        private void OnLogAdded()
        {
            if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownFinished) return;

            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    var view = CollectionViewSource.GetDefaultView(LogList.ItemsSource);
                    view?.Refresh();

                    TxtCount.Text = FormatCount();

                    if (_stickToBottom && LogList.Items.Count > 0)
                        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Erro no console: {ex.Message}");
                }
            }));
        }

        private string FormatCount()
        {
            long total = LogStore.TotalLines;
            if (total == 0) return "0 linhas";
            return $"{total:N0} linhas em disco | {ConsoleManager.Logs.Count:N0} na memoria";
        }

        // Scroll "inteligente": detecta quando o usuário sobe para investigar algo.
        private void LogList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            var sv = e.OriginalSource as ScrollViewer;
            if (sv == null) return;

            if (e.VerticalChange != 0 || e.ExtentHeightChange != 0)
            {
                // Usuário no rodapé? (tolerância de ~24px evita oscilar no wrap)
                _stickToBottom = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 24.0;
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            var view = CollectionViewSource.GetDefaultView(LogList.ItemsSource);
            if (view == null) return;

            var term = TxtSearch.Text.Trim();
            if (string.IsNullOrEmpty(term))
            {
                view.Filter = null;
                TxtTitleStatus.Text = " | Logs ILIMITADOS (virtualizado)";
                TxtTitleStatus.Foreground = System.Windows.Media.Brushes.Gray;
            }
            else
            {
                view.Filter = (o) => o is string s &&
                                     s.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
                TxtTitleStatus.Text = $" | Buscando: \"{term}\"";
                TxtTitleStatus.Foreground = System.Windows.Media.Brushes.Orange;
            }
            view.Refresh();
        }

        private void BtnCopySelection_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int count = LogList.SelectedItems.Count;
                if (count == 0)
                {
                    ConsoleManager.WriteLine("Nenhuma linha selecionada. Ctrl+clique ou arraste para selecionar.");
                    return;
                }

                var sb = new StringBuilder();
                foreach (var item in LogList.SelectedItems)
                {
                    if (item is string s)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(s);
                    }
                }

                Clipboard.SetText(sb.ToString());
                ConsoleManager.WriteLine($"Copiadas {count} linha(s) selecionada(s).");
            }
            catch (Exception ex)
            {
                ConsoleManager.WriteLine($"Erro ao copiar seleção: {ex.Message}");
            }
        }

        // Ctrl+C copia a seleção do log mesmo com o foco no botão/barra; Ctrl+A seleciona
        // tudo; Esc limpa a busca.
        private void GlobalConsole_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

            if (e.Key == Key.C && ctrl)
            {
                if (TxtSearch.IsKeyboardFocusWithin)
                {
                    // Deixa o TextBox de busca copiar o próprio texto (comportamento nativo).
                    return;
                }
                if (LogList.SelectedItems.Count > 0)
                {
                    BtnCopySelection_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
            }
            else if (ctrl && e.Key == Key.A)
            {
                if (TxtSearch.IsKeyboardFocusWithin) return; // deixa o nativo do TextBox
                LogList.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && !string.IsNullOrEmpty(TxtSearch.Text))
            {
                TxtSearch.Clear();
                e.Handled = true;
            }
        }

        private async void BtnCopyAll_Click(object sender, RoutedEventArgs e)
        {
            if (_copyAllInFlight) return;
            _copyAllInFlight = true;
            try
            {
                // Lê o log COMPLETO do arquivo (não da UI) — o limite de 500 não existe mais.
                var fullText = await System.Threading.Tasks.Task.Run(() => LogStore.GetFullText());

                if (string.IsNullOrEmpty(fullText))
                {
                    ConsoleManager.WriteLine("Nenhum log disponível para copiar.");
                    return;
                }

                int lines = CountLines(fullText);
                Clipboard.SetText(fullText);
                ConsoleManager.WriteLine($"Copiado log completo ({lines:N0} linhas) para a área de transferência.");
            }
            catch (Exception ex)
            {
                ConsoleManager.WriteLine($"Erro ao copiar log completo: {ex.Message}");
            }
            finally
            {
                _copyAllInFlight = false;
            }
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int n = 1;
            foreach (char c in text)
                if (c == '\n') n++;
            return n;
        }

        // ---- Menu de contexto (clique com o botão direito no log) ----

        private void MnuCopySelection_Click(object sender, RoutedEventArgs e)
        {
            BtnCopySelection_Click(sender, e);
        }

        private void MnuCopyAll_Click(object sender, RoutedEventArgs e)
        {
            BtnCopyAll_Click(sender, e);
        }

        private void MnuSelectAll_Click(object sender, RoutedEventArgs e)
        {
            LogList.SelectAll();
        }

        private void MnuClear_Click(object sender, RoutedEventArgs e)
        {
            BtnClear_Click(sender, e);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ConsoleManager.Clear();
                ConsoleManager.WriteLine("Console limpo.");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Erro ao limpar console: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateStatusDisplay()
        {
            TxtTitleStatus.Text = " | Logs ILIMITADOS (virtualizado)";
            TxtTitleStatus.Foreground = System.Windows.Media.Brushes.Gray;
            TxtCount.Text = FormatCount();
        }

        // Boa prática: Desinscrever eventos ao destruir o controle para não vazar memória
        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            ConsoleManager.OnLogAdded -= OnLogAdded;
            PreviewKeyDown -= GlobalConsole_PreviewKeyDown;
        }
    }
}