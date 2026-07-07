using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using KitLugia.GUI.Extensions;

// --- CORREÇÃO DE AMBIGUIDADE ---
using TextBox = System.Windows.Controls.TextBox;

namespace KitLugia.GUI
{
    /// <summary>
    /// Emulador de Terminal para rodar lógica Legacy dentro do WPF.
    /// Substitui System.Console.
    /// </summary>
    public static class VirtualTerminal
    {
        private static TextBlock? _outputBlock;
        private static ScrollViewer? _scroller;
        private static TextBox? _inputBox;

        // Esta é a mágica: uma tarefa que fica pendente até você apertar Enter
        private static TaskCompletionSource<string>? _inputTask;

        /// <summary>
        /// Conecta o código lógico aos controles visuais da tela.
        /// </summary>
        public static void Initialize(TextBlock output, ScrollViewer scroller, TextBox input)
        {
            _outputBlock = output;
            _scroller = scroller;
            _inputBox = input;
        }

        /// <summary>
        /// Substitui Console.WriteLine()
        /// </summary>
        public static void WriteLine(string text = "")
        {
            Write(text + "\n");
        }

        /// <summary>
        /// Substitui Console.Write()
        /// </summary>
        public static void Write(string text)
        {
            if (_outputBlock?.Dispatcher == null || _outputBlock.Dispatcher.HasShutdownFinished) return;

            _outputBlock.Dispatcher.Invoke(() =>
            {
                _outputBlock.Text += text;
                _scroller?.ScrollToBottom();
            });
        }

        /// <summary>
        /// Substitui Console.Clear()
        /// </summary>
        public static void Clear()
        {
            if (_outputBlock?.Dispatcher == null || _outputBlock.Dispatcher.HasShutdownFinished) return;
            _outputBlock.Dispatcher.Invoke(() => _outputBlock.Text = "");
        }

        /// <summary>
        /// Substitui Console.ReadLine().
        /// O código vai PAUSAR aqui (await) até o usuário digitar e dar Enter na GUI.
        /// </summary>
        public static async Task<string> ReadLineAsync()
        {
            if (_inputBox?.Dispatcher == null || _inputBox.Dispatcher.HasShutdownFinished) return "";

            _inputTask?.TrySetCanceled();
            _inputTask = new TaskCompletionSource<string>();

            _inputBox.Dispatcher.Invoke(() =>
            {
                _inputBox.IsEnabled = true;
                _inputBox.Focus();
            });

            string result = await _inputTask.Task;

            if (!_inputBox.Dispatcher.HasShutdownFinished)
            {
                _inputBox.Dispatcher.Invoke(() =>
                {
                    _inputBox.IsEnabled = false;
                });
            }

            return result;
        }

        /// <summary>
        /// Chamado pelo "InteractiveTerminal.xaml.cs" quando o usuário aperta ENTER.
        /// </summary>
        public static void SubmitInput(string text)
        {
            _inputTask?.TrySetResult(text);
        }

        /// <summary>
        /// Limpa as referências estáticas para permitir GC dos controles UI
        /// </summary>
        public static void Cleanup()
        {
            _outputBlock = null;
            _scroller = null;
            _inputBox = null;
            _inputTask = null;
        }
    }
}