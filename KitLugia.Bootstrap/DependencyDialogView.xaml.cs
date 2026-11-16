using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KitLugia.Bootstrap
{
    /// <summary>Uma linha da lista de componentes (nome + detalhe).</summary>
    public sealed class ComponentRow
    {
        public string Name { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// Conteudo da tela "Componentes Adicionais Necesarios".
    /// Fica separado da Window para poder ser renderizado OFF-SCREEN no
    /// modo --shot (prova visual sem abrir janela na tela do usuario).
    /// </summary>
    public partial class DependencyDialogView : UserControl
    {
        /// <summary>Disparado quando o usuario clica em INSTALAR.</summary>
        public event System.EventHandler InstallRequested;

        /// <summary>Disparado quando o usuario clica em CANCELAR.</summary>
        public event System.EventHandler CancelRequested;

        /// <summary>Disparado quando o usuario fecha a tela pelo X.</summary>
        public event System.EventHandler CloseRequested;

        public DependencyDialogView()
        {
            InitializeComponent();
            ProgressTrack.SizeChanged += (s, e) => UpdateFillWidth();
        }

        /// <summary>Preenche a lista de componentes que faltam.</summary>
        public void SetComponents(IEnumerable<RequiredComponent> components)
        {
            var rows = new List<ComponentRow>();
            foreach (var c in components)
            {
                rows.Add(new ComponentRow
                {
                    Name = c.DisplayName,
                    Detail = c.Detail + "  •  não encontrado neste computador"
                });
            }
            ListComponents.ItemsSource = rows;
        }

        /// <summary>Atualiza a barra de progresso e o texto de status.</summary>
        public void SetProgress(double percent, string status)
        {
            PanelProgress.Visibility = Visibility.Visible;
            TxtStatus.Text = status ?? "";
            TxtPercent.Text = Math.Max(0, Math.Min(100, percent)).ToString("0") + "%";
            UpdateFillWidth();
        }

        /// <summary>Mostra uma mensagem de erro (mantem o botao INSTALAR disponivel).</summary>
        public void ShowError(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                TxtError.Text = "";
                TxtError.Visibility = Visibility.Collapsed;
                return;
            }
            TxtError.Text = message;
            TxtError.Visibility = Visibility.Visible;
        }

        /// <summary>Bloqueia a tela durante a instalacao (evita clique duplo).</summary>
        public void SetBusy(bool busy, string label = null)
        {
            BtnInstall.IsEnabled = !busy;
            BtnCancel.IsEnabled = !busy;
            BtnInstall.Content = busy ? (label ?? "Instalando...") : "Instalar";
            if (busy)
            {
                TxtHint.Text = "Não feche esta janela. Uma confirmação de administrador (UAC) pode aparecer.";
            }
            else
            {
                TxtHint.Text = "Download oficial da Microsoft.";
            }
        }

        /// <summary>Estado final: sucesso (verde) ou erro.</summary>
        public void SetDone(bool success, string message)
        {
            TxtStatus.Text = message ?? "";
            TxtPercent.Text = success ? "100%" : "";
            TxtStatus.Foreground = success
                ? (Brush)FindResource("AccentColor")
                : (Brush)FindResource("TextSecondary");
        }

        private void UpdateFillWidth()
        {
            double track = ProgressTrack.ActualWidth;
            if (track <= 0) return;
            double pct = 0;
            try
            {
                string raw = (TxtPercent.Text ?? "0").Replace("%", "").Trim();
                double parsed;
                if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    pct = parsed;
            }
            catch { pct = 0; }
            ProgressFill.Width = Math.Max(0, track * pct / 100.0);
        }

        // ── Eventos da UI ────────────────────────────────────────────────

        private void TitleBar_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var win = Window.GetWindow(this);
                if (win != null) win.DragMove();
            }
            catch { }
        }

        private void BtnClose_Click(object sender, MouseButtonEventArgs e)
        {
            CloseRequested?.Invoke(this, System.EventArgs.Empty);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke(this, System.EventArgs.Empty);
        }

        private void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            InstallRequested?.Invoke(this, System.EventArgs.Empty);
        }
    }
}
