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
            if (_outputBlock == null) return;

            // Garante que rode na Thread da UI para não travar
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
            if (_outputBlock == null) return;
            _outputBlock.Dispatcher.Invoke(() => _outputBlock.Text = "");
        }

        /// <summary>
        /// Substitui Console.ReadLine().
        /// O código vai PAUSAR aqui (await) até o usuário digitar e dar Enter na GUI.
        /// </summary>
        public static async Task<string> ReadLineAsync()
        {
            if (_inputBox == null) return "";

            // 1. Destrava a caixa de texto e foca nela
            _inputBox.Dispatcher.Invoke(() =>
            {
                _inputBox.IsEnabled = true;
                _inputBox.Focus();
            });

            // 2. Cria uma "promessa" de que um texto virá no futuro
            _inputTask = new TaskCompletionSource<string>();

            // 3. Espera (await) até a promessa ser cumprida no método SubmitInput
            string result = await _inputTask.Task;

            // 4. Trava a caixa de texto de novo
            _inputBox.Dispatcher.Invoke(() =>
            {
                _inputBox.IsEnabled = false;
            });

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