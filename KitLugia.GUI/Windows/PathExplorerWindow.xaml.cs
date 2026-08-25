using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using KitLugia.Core;

namespace KitLugia.GUI.Windows
{
    /// <summary>
    /// Janela "Explorador de PATH": mostra o PATH atual (System/User) com o
    /// diagnostico de cada entrada, o que o kit considera ausente (essenciais
    /// de sistema + programas instalados detectados) e adiciona por item.
    /// </summary>
    public partial class PathExplorerWindow : Window
    {
        public PathExplorerWindow()
        {
            InitializeComponent();
            _ = LoadDataAsync();
        }

        public class PathEntryRow
        {
            public string Icon { get; set; } = "";
            public string Path { get; set; } = "";
            public string Color { get; set; } = "";
            public string Detail { get; set; } = "";
        }

        public class PathCandidateRow
        {
            public string Label { get; set; } = "";
            public string Path { get; set; } = "";
            public string Detail { get; set; } = "";
            public bool CanAdd { get; set; }
        }

        private async Task LoadDataAsync()
        {
            TxtNote.Text = "Carregando PATH...";
            try
            {
                var data = await Task.Run(() => LoadDataCore(msg =>
                    Dispatcher.Invoke(() => TxtNote.Text = msg)));

                TxtSystemRaw.Text = data.SystemRaw;
                TxtUserRaw.Text = data.UserRaw;
                SysEntriesList.ItemsSource = data.SystemRows;
                UsrEntriesList.ItemsSource = data.UserRows;
                SysMissingList.ItemsSource = data.SystemMissing;
                UsrMissingList.ItemsSource = data.UserMissing;

                // Probe do indexador nativo (abertura crua do volume) em background.
                bool usn = data.UsnAvailable;
                TxtNote.Text = usn
                    ? "💡 Indexador nativo ativo (USN/MFT embutido): o kit lê a Master File Table direto do volume para achar programas ausentes (~1-3s na primeira vez, depois cacheado)."
                    : "💡 Dica: rode o kit como administrador para ativar o indexador nativo USN/MFT - o kit acha programas ausentes lendo a Master File Table direto do volume em ~1-3s.";
            }
            catch (Exception ex)
            {
                TxtNote.Text = $"Falha ao carregar o PATH: {ex.Message}";
            }
        }

        private (string SystemRaw, string UserRaw, List<PathEntryRow> SystemRows, List<PathEntryRow> UserRows,
                 List<PathCandidateRow> SystemMissing, List<PathCandidateRow> UserMissing,
                 bool UsnAvailable) LoadDataCore(Action<string>? progress = null)
        {
            string sysRaw = PathRepair.GetSystemPathValue();
            string usrRaw = PathRepair.GetUserPathValue();

            var sysRows = PathRepair.DiagnosePath(sysRaw, "System").Select(ToRow).ToList();
            var usrRows = PathRepair.DiagnosePath(usrRaw, "User").Select(ToRow).ToList();

            var sysMissing = PathRepair.GetMissingSystemEntries(sysRaw)
                .Select(c => new PathCandidateRow { Label = c.Label, Path = c.Path, Detail = c.Detail, CanAdd = c.CanAdd })
                .ToList();
            progress?.Invoke("Detectando programas instalados...");
            var usrMissing = PathRepair.GetMissingInstalledEntries(usrRaw, progress)
                .Select(c => new PathCandidateRow { Label = c.Label, Path = c.Path, Detail = c.Detail, CanAdd = c.CanAdd })
                .ToList();

            bool usn = NativeUsn.IsAvailable;

            return (sysRaw, usrRaw, sysRows, usrRows, sysMissing, usrMissing, usn);
        }

        private static PathEntryRow ToRow(PathEntry e)
        {
            var (icon, color) = e.Problem switch
            {
                PathEntryProblem.None => ("✅", "#4CAF50"),
                PathEntryProblem.Missing => ("⚠️", "#FFA500"),
                PathEntryProblem.WrongLocation => ("🔄", "#FFD700"),
                PathEntryProblem.Duplicate => ("🔁", "#FF6F61"),
                PathEntryProblem.Junk => ("🧹", "#999999"),
                PathEntryProblem.Orphan => ("🗑️", "#999999"),
                PathEntryProblem.SyntaxError => ("❌", "#FF6F61"),
                _ => ("❔", "#999999")
            };
            string detail = string.IsNullOrEmpty(e.ProblemDetail) ? "OK" : $"{e.ProblemDetail} - {e.RecommendedAction}";
            return new PathEntryRow { Icon = icon, Path = e.CleanValue, Color = color, Detail = detail };
        }

        private void RefreshSystemSection()
        {
            string sysRaw = PathRepair.GetSystemPathValue();
            var sysRows = PathRepair.DiagnosePath(sysRaw, "System").Select(ToRow).ToList();
            var sysMissing = PathRepair.GetMissingSystemEntries(sysRaw)
                .Select(c => new PathCandidateRow { Label = c.Label, Path = c.Path, Detail = c.Detail, CanAdd = c.CanAdd })
                .ToList();

            Dispatcher.Invoke(() =>
            {
                TxtSystemRaw.Text = sysRaw;
                SysEntriesList.ItemsSource = sysRows;
                SysMissingList.ItemsSource = sysMissing;
            });
        }

        private void RefreshUserSection()
        {
            string usrRaw = PathRepair.GetUserPathValue();
            var usrRows = PathRepair.DiagnosePath(usrRaw, "User").Select(ToRow).ToList();
            Action<string> progress = msg => Dispatcher.Invoke(() => TxtNote.Text = msg);
            var usrMissing = PathRepair.GetMissingInstalledEntries(usrRaw, progress)
                .Select(c => new PathCandidateRow { Label = c.Label, Path = c.Path, Detail = c.Detail, CanAdd = c.CanAdd })
                .ToList();

            Dispatcher.Invoke(() =>
            {
                TxtUserRaw.Text = usrRaw;
                UsrEntriesList.ItemsSource = usrRows;
                UsrMissingList.ItemsSource = usrMissing;
            });
        }

        private async void BtnAddSystem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is PathCandidateRow row)
            {
                if (!ConfirmIfFolderMissing(row)) return;
                btn.IsEnabled = false;
                btn.Content = "⏳ ...";
                try
                {
                    bool ok = await Task.Run(() => PathRepair.AddSinglePathEntry("System", row.Path));
                    if (ok)
                    {
                        btn.Content = "✔ Adicionado";
                        await Task.Run(RefreshSystemSection);
                    }
                    else
                    {
                        btn.Content = "Adicionar";
                        btn.IsEnabled = true;
                        System.Windows.MessageBox.Show($"Não foi possível adicionar '{row.Path}' ao PATH do Sistema.",
                            "Explorador de PATH", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch
                {
                    btn.Content = "Adicionar";
                    btn.IsEnabled = true;
                }
            }
        }

        private async void BtnAddUser_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is PathCandidateRow row)
            {
                if (!ConfirmIfFolderMissing(row)) return;
                btn.IsEnabled = false;
                btn.Content = "⏳ ...";
                try
                {
                    bool ok = await Task.Run(() => PathRepair.AddSinglePathEntry("User", row.Path));
                    if (ok)
                    {
                        btn.Content = "✔ Adicionado";
                        await Task.Run(RefreshUserSection);
                    }
                    else
                    {
                        btn.Content = "Adicionar";
                        btn.IsEnabled = true;
                        System.Windows.MessageBox.Show($"Não foi possível adicionar '{row.Path}' ao PATH do Usuário.",
                            "Explorador de PATH", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch
                {
                    btn.Content = "Adicionar";
                    btn.IsEnabled = true;
                }
            }
        }

        private static bool ConfirmIfFolderMissing(PathCandidateRow row)
        {
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(row.Path).TrimEnd('\\');
                if (Directory.Exists(expanded)) return true;
            }
            catch { return true; }
            return System.Windows.MessageBox.Show(
                $"A pasta '{row.Path}' não existe no PC neste momento.\n\nAdicionar mesmo assim? (O PATH é só texto - a entrada passa a valer quando a pasta for instalada/criada)",
                "Explorador de PATH", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private void BtnCopySystem_Click(object sender, RoutedEventArgs e) => CopyToClipboard(TxtSystemRaw.Text);

        private void BtnCopyUser_Click(object sender, RoutedEventArgs e) => CopyToClipboard(TxtUserRaw.Text);

        private void CopyToClipboard(string text)
        {
            try { System.Windows.Clipboard.SetText(text); }
            catch { }
        }

        private void BtnOpenSystemEnv_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("rundll32.exe", "sysdm.cpl,EditEnvironmentVariables")
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnClose_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => Close();

        private void BtnGuide_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                var guide = new PathGuideWindow { Owner = this };
                guide.ShowDialog();
            }
            catch
            {
            }
        }
    }
}