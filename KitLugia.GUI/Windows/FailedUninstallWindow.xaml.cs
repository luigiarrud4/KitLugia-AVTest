using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KitLugia.Core;

namespace KitLugia.GUI.Windows
{
    /// <summary>
    /// Janela mostrada quando o desinstalador do programa FALHOU. Lista SOMENTE
    /// itens com 100% de certeza de que pertencem ao programa (AutoCleanup 100/100
    /// para arquivos/pastas; chave Uninstall exata do app, pasta SOFTWARE com nome
    /// exato, ou classificacao Safe para o registro). A limpeza manual sempre vai
    /// para a Lixeira com backup (fail-closed) — nunca apaga permanentemente.
    /// </summary>
    public partial class FailedUninstallWindow : Window
    {
        public sealed class Item : INotifyPropertyChanged
        {
            private bool _selected = true;
            public string Path { get; set; } = "";
            public bool IsSelected
            {
                get => _selected;
                set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
            }
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private readonly string _displayName;
        private readonly string _installLocation;
        private readonly string _cleanName;
        private readonly string _ownRegistryPath;
        private readonly List<Item> _folders = new();
        private readonly List<Item> _files = new();
        private readonly List<Item> _regs = new();
        private int _ignoredFiles;
        private int _ignoredReg;

        public FailedUninstallWindow(string displayName, string installLocation, string ownRegistryPath,
                                     string errorText, IEnumerable<string> fileCandidates, IEnumerable<string> regCandidates)
        {
            InitializeComponent();
            _displayName = displayName ?? "";
            _installLocation = installLocation ?? "";
            _ownRegistryPath = ownRegistryPath ?? "";
            _cleanName = StripVersion(_displayName);

            TitleText.Text = $"⚠️ Desinstalação não concluída — {_displayName}";
            ErrorText.Text = string.IsNullOrWhiteSpace(errorText)
                ? "O desinstalador não retornou sucesso. O programa pode continuar instalado."
                : "Motivo reportado: " + errorText;

            foreach (var p in (fileCandidates ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (ConfiancaArquivo(p) >= 100)
                {
                    var item = new Item { Path = p };
                    item.PropertyChanged += (_, _) => UpdateCleanButton();
                    if (Directory.Exists(p)) _folders.Add(item); else _files.Add(item);
                }
                else _ignoredFiles++;
            }

            foreach (var p in (regCandidates ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (RegistroCerteza(p))
                {
                    var item = new Item { Path = p };
                    item.PropertyChanged += (_, _) => UpdateCleanButton();
                    _regs.Add(item);
                }
                else _ignoredReg++;
            }

            FolderList.ItemsSource = _folders;
            FileList.ItemsSource = _files;
            RegList.ItemsSource = _regs;

            FoldersHint.Text = $"{_folders.Count} pasta(s) — {_ignoredFiles} outro(s) item(ns) de arquivo ignorado(s) por não serem 100%.";
            FilesHint.Text = $"{_files.Count} arquivo(s) soltos.";
            RegHint.Text = $"{_regs.Count} chave(s) — {_ignoredReg} outro(s) item(ns) de registro ignorado(s) por não serem 100%.";

            FoldersEmpty.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FilesEmpty.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RegEmpty.Visibility = _regs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateCleanButton();
        }

        private static string StripVersion(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            return Regex.Replace(name.Trim(), @"\s+\d+(\.\d+)*\s*$", "").Trim();
        }

        private int ConfiancaArquivo(string path)
        {
            int c1 = AutoCleanup.ConfiancaDe(_displayName, _installLocation, path);
            if (c1 >= 100) return c1;
            // displayName pode vir com versão ("DuoDesk 2.5.0") enquanto a pasta é "DuoDesk"
            if (_cleanName != _displayName)
                return Math.Max(c1, AutoCleanup.ConfiancaDe(_cleanName, _installLocation, path));
            return c1;
        }

        private bool RegistroCerteza(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            // 1) chave Uninstall exata do próprio app
            if (!string.IsNullOrEmpty(_ownRegistryPath) &&
                path.Equals(_ownRegistryPath, StringComparison.OrdinalIgnoreCase))
                return true;
            // 2) classificador do motor marca como Safe (HKCU\Software\<App>, Installer\...)
            if (DeepUninstaller.ClassifyRegistrySafety(_displayName, _installLocation, path) == CleanupSafety.Safe)
                return true;
            // 3) algum segmento do caminho é EXATAMENTE o nome do app (sem versão)
            var segs = path.Split('\\');
            foreach (var s in segs)
                if (s.Equals(_cleanName, StringComparison.OrdinalIgnoreCase) || s.Equals(_displayName, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private void UpdateCleanButton()
        {
            int n = _folders.Count(i => i.IsSelected) + _files.Count(i => i.IsSelected) + _regs.Count(i => i.IsSelected);
            BtnClean.Content = $"🗑️ Limpar selecionados ({n})";
            BtnClean.IsEnabled = n > 0;
        }

        private async void BtnClean_Click(object sender, RoutedEventArgs e)
        {
            var files = _folders.Concat(_files).Where(i => i.IsSelected).Select(i => i.Path).ToList();
            var regs = _regs.Where(i => i.IsSelected).Select(i => i.Path).ToList();
            if (files.Count == 0 && regs.Count == 0) return;

            var ask = $"Enviar {files.Count} arquivo(s)/pasta(s) e {regs.Count} chave(s) de registro para a Lixeira?\n\n" +
                      "Tudo passa por backup antes e pode ser restaurado na aba Histórico. Nada é apagado permanentemente.";
            if (System.Windows.MessageBox.Show(this, ask, "Confirmar limpeza (Lixeira)", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            BtnClean.IsEnabled = false;
            StatusText.Text = "Limpando (backup + Lixeira)...";
            Cursor = System.Windows.Input.Cursors.Wait;
            try
            {
                var result = new DeepUninstaller.UninstallResult();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                await Task.Run(() => DeepUninstaller.PerformCleanup(
                    files, regs, result,
                    displayName: _displayName, installLocation: _installLocation,
                    ct: cts.Token, forceToRecycleBin: true), cts.Token);

                // Remove da lista SOMENTE o que realmente sumiu (fail-closed: o que
                // ficou continua visível para nova tentativa).
                foreach (var item in _folders.Concat(_files).Where(i => i.IsSelected).ToList())
                {
                    bool gone = !File.Exists(item.Path) && !Directory.Exists(item.Path);
                    if (gone) { _folders.Remove(item); _files.Remove(item); }
                }
                foreach (var item in _regs.Where(i => i.IsSelected).ToList())
                {
                    bool gone = true;
                    try { gone = !DeepUninstaller.RegistryItemStillExists(item.Path); } catch { }
                    if (gone) _regs.Remove(item);
                }

                FolderList.ItemsSource = null; FolderList.ItemsSource = _folders;
                FileList.ItemsSource = null; FileList.ItemsSource = _files;
                RegList.ItemsSource = null; RegList.ItemsSource = _regs;

                FoldersEmpty.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                FilesEmpty.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                RegEmpty.Visibility = _regs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                int kept = result.FilesKept.Count;
                string resumo = $"✅ {result.FilesDeleted + result.RegistryDeleted} item(ns) foram para a Lixeira (backup feito).";
                if (kept > 0 || result.DeleteErrors.Count > 0)
                    resumo += $"\n⚠️ {kept + result.DeleteErrors.Count} item(ns) NÃO foram removidos (backup falhou ou cancelado) e continuam na lista.";
                if (result.Errors.Count > 0)
                    resumo += "\nErros: " + string.Join("; ", result.Errors.Take(3));
                StatusText.Text = resumo;
                KitLugia.Core.Logger.Log($"🗑️ Limpeza manual pós-falha de '{_displayName}': {result.FilesDeleted}F/{result.RegistryDeleted}R para a Lixeira, {kept} preservados.");
            }
            catch (Exception ex)
            {
                StatusText.Text = "Erro na limpeza: " + ex.Message;
                KitLugia.Core.Logger.LogError("FailedUninstallWindow.Clean", ex.ToString());
            }
            finally
            {
                Cursor = System.Windows.Input.Cursors.Arrow;
                UpdateCleanButton();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
