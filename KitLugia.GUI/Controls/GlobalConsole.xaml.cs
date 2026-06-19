using System;
using System.Windows;
using System.Windows.Controls;
using KitLugia.GUI;

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

        public GlobalConsole()
        {
            InitializeComponent();
            
            // Sincroniza com o ConsoleManager
            ConsoleManager.OnLogAdded += UpdateTextBox;
            
            // Estado inicial do checkbox
            ChkRemoveLimit.IsChecked = KitLugia.Core.Logger.DisableOutputLimit;
            
            // Atualiza status inicial
            UpdateStatusDisplay();
        }

        private void UpdateTextBox()
        {
            // Simples e direto - sem otimizações que causam loops
            if (Application.Current != null)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        // Pega todos os logs do ConsoleManager
                        var allLogs = string.Join("\n", ConsoleManager.Logs);
                        
                        // Atualiza TextBox
                        TxtLog.Text = allLogs;
                        
                        // Scroll automático
                        if (LogScroller.IsLoaded)
                        {
                            LogScroller.ScrollToEnd();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Erro no console: {ex.Message}");
                    }
                });
            }
        }

        private void TxtLog_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Scroll automático simples
            if (TxtLog.IsLoaded)
            {
                LogScroller.ScrollToEnd();
            }
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(TxtLog.Text))
                {
                    ConsoleManager.WriteLine("📋 Nenhum log disponível para copiar.");
                    return;
                }

                Clipboard.SetText(TxtLog.Text);
                ConsoleManager.WriteLine("📋 Logs copiados para área de transferência!");
            }
            catch (Exception ex)
            {
                ConsoleManager.WriteLine($"❌ Erro ao copiar logs: {ex.Message}");
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ConsoleManager.Clear();
                TxtLog.Clear();
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

        // Handlers para o checkbox de limite de logs
        private void ChkRemoveLimit_Checked(object sender, RoutedEventArgs e)
        {
            KitLugia.Core.Logger.DisableOutputLimit = true;
            ConsoleManager.WriteLine("🔓 LIMITE DE 500 LINHAS REMOVIDO - Logs completos serão capturados");
            UpdateStatusDisplay();
        }

        private void ChkRemoveLimit_Unchecked(object sender, RoutedEventArgs e)
        {
            KitLugia.Core.Logger.DisableOutputLimit = false;
            ConsoleManager.WriteLine("🔒 LIMITE DE 500 LINHAS ATIVADO - Logs serão truncados");
            UpdateStatusDisplay();
        }

        private void UpdateStatusDisplay()
        {
            if (KitLugia.Core.Logger.DisableOutputLimit)
            {
                TxtTitleStatus.Text = " | Logs ILIMITADOS";
                TxtTitleStatus.Foreground = System.Windows.Media.Brushes.Orange;
            }
            else
            {
                TxtTitleStatus.Text = " | Monitorando...";
                TxtTitleStatus.Foreground = System.Windows.Media.Brushes.Gray;
            }
        }

        // Boa prática: Desinscrever eventos ao destruir o controle para não vazar memória
        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            ConsoleManager.OnLogAdded -= UpdateTextBox;
        }
    }
}
