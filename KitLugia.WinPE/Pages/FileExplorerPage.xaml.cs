using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KitLugia.WinPE.Pages
{
    public class FileEntry
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string Size { get; set; } = "";
        public string Type { get; set; } = "";
        public string Modified { get; set; } = "";
        public bool IsDirectory { get; set; }
    }

    public partial class FileExplorerPage : Page
    {
        private string _currentDir = "";
        private readonly ObservableCollection<FileEntry> _files = new();
        private readonly Stack<string> _backStack = new();
        private string? _copiedPath;

        public FileExplorerPage()
        {
            InitializeComponent();
            FileList.ItemsSource = _files;
            Loaded += (_, _) => LoadDrives();
        }

        private void LoadDrives()
        {
            FolderTree.Items.Clear();
            foreach (var di in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                string label = NativeMethods.GetDriveLabel(di.Name);
                string icon = di.DriveType switch
                {
                    DriveType.Fixed => "💾",
                    DriveType.Removable => "🔌",
                    DriveType.CDRom => "💿",
                    DriveType.Network => "🌐",
                    _ => "📁"
                };
                string title = $"{icon} {di.Name.TrimEnd('\\')}  [{label}]";
                var item = new TreeViewItem
                {
                    Header = title,
                    Tag = di.Name,
                    FontSize = 13
                };
                item.Items.Add("...");
                item.Expanded += DriveItem_Expanded;
                FolderTree.Items.Add(item);
            }
        }

        private void DriveItem_Expanded(object sender, RoutedEventArgs e)
        {
            var item = (TreeViewItem)sender;
            item.Items.Clear();
            string path = (item.Tag as string) ?? "";
            try
            {
                foreach (var dir in Directory.GetDirectories(path))
                {
                    var di = new DirectoryInfo(dir);
                    var child = new TreeViewItem
                    {
                        Header = $"📂 {di.Name}",
                        Tag = dir,
                        FontSize = 13
                    };
                    try
                    {
                        if (Directory.GetDirectories(dir).Length > 0)
                            child.Items.Add("...");
                    }
                    catch { }
                    child.Expanded += FolderItem_Expanded;
                    item.Items.Add(child);
                }
            }
            catch { }
        }

        private void FolderItem_Expanded(object sender, RoutedEventArgs e)
        {
            var item = (TreeViewItem)sender;
            item.Items.Clear();
            string path = (item.Tag as string) ?? "";
            try
            {
                foreach (var dir in Directory.GetDirectories(path))
                {
                    var di = new DirectoryInfo(dir);
                    var child = new TreeViewItem
                    {
                        Header = $"📂 {di.Name}",
                        Tag = dir,
                        FontSize = 13
                    };
                    try
                    {
                        if (Directory.GetDirectories(dir).Length > 0)
                            child.Items.Add("...");
                    }
                    catch { }
                    child.Expanded += FolderItem_Expanded;
                    item.Items.Add(child);
                }
            }
            catch { }
        }

        private void FolderTree_SelectedItemChanged(object sender, RoutedEventArgs e)
        {
            if (FolderTree.SelectedItem is TreeViewItem tvi && tvi.Tag is string path && Directory.Exists(path))
            {
                _backStack.Push(_currentDir);
                BtnBack.IsEnabled = _backStack.Count > 0;
                NavigateToDir(path);
            }
        }

        private void NavigateToDir(string path)
        {
            try
            {
                _currentDir = path;
                CurrentPath.Text = path;
                _files.Clear();
                StatusBar.Text = $"Carregando {path}...";

                var items = new List<FileEntry>();

                try
                {
                    foreach (var dir in Directory.GetDirectories(path))
                    {
                        var di = new DirectoryInfo(dir);
                        items.Add(new FileEntry
                        {
                            Name = di.Name,
                            DisplayName = $"📂 {di.Name}",
                            FullPath = dir,
                            Size = "",
                            Type = "Pasta",
                            Modified = di.LastWriteTime.ToString("dd/MM/yy HH:mm"),
                            IsDirectory = true
                        });
                    }
                }
                catch { }

                try
                {
                    foreach (var file in Directory.GetFiles(path))
                    {
                        var fi = new FileInfo(file);
                        items.Add(new FileEntry
                        {
                            Name = fi.Name,
                            DisplayName = $"📄 {fi.Name}",
                            FullPath = file,
                            Size = FormatSize(fi.Length),
                            Type = fi.Extension.ToUpperInvariant() switch
                            {
                                ".exe" => "Aplicativo",
                                ".dll" => "Biblioteca",
                                ".txt" => "Texto",
                                ".cmd" => "Script",
                                ".bat" => "Script",
                                ".ps1" => "PowerShell",
                                ".zip" => "Arquivo ZIP",
                                ".7z" => "Arquivo 7z",
                                ".rar" => "Arquivo RAR",
                                ".wim" => "Imagem WIM",
                                ".iso" => "Imagem ISO",
                                ".png" => "Imagem PNG",
                                ".jpg" or ".jpeg" => "Imagem JPEG",
                                ".bmp" => "Imagem BMP",
                                ".xml" => "XML",
                                ".json" => "JSON",
                                _ => fi.Extension.ToUpperInvariant().TrimStart('.') + " File"
                            },
                            Modified = fi.LastWriteTime.ToString("dd/MM/yy HH:mm"),
                            IsDirectory = false
                        });
                    }
                }
                catch { }

                items = items.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name).ToList();
                foreach (var item in items)
                    _files.Add(item);

                StatusBar.Text = $"{items.Count} itens | {path}";
            }
            catch (Exception ex)
            {
                StatusBar.Text = $"Erro: {ex.Message}";
            }
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is FileEntry fe)
            {
                if (fe.IsDirectory)
                {
                    _backStack.Push(_currentDir);
                    BtnBack.IsEnabled = _backStack.Count > 0;
                    NavigateToDir(fe.FullPath);
                }
                else
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(fe.FullPath)
                        { UseShellExecute = true };
                        System.Diagnostics.Process.Start(psi);
                    }
                    catch { StatusBar.Text = $"Não foi possível abrir: {fe.Name}"; }
                }
            }
        }

        private void BtnBack_Click(object _, RoutedEventArgs e)
        {
            if (_backStack.Count > 0)
            {
                string prev = _backStack.Pop();
                BtnBack.IsEnabled = _backStack.Count > 0;
                NavigateToDir(prev);
            }
        }

        private void BtnUp_Click(object _, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentDir))
            {
                string? parent = Directory.GetParent(_currentDir)?.FullName;
                if (parent != null)
                {
                    _backStack.Push(_currentDir);
                    BtnBack.IsEnabled = _backStack.Count > 0;
                    NavigateToDir(parent);
                }
            }
        }

        private void BtnCopy_Click(object _, RoutedEventArgs e)
        {
            if (FileList.SelectedItem is FileEntry fe)
            {
                _copiedPath = fe.FullPath;
                StatusBar.Text = $"Copiado: {fe.Name}";
            }
        }

        private void BtnPaste_Click(object _, RoutedEventArgs e)
        {
            if (_copiedPath == null || string.IsNullOrEmpty(_currentDir))
                return;

            string dest = Path.Combine(_currentDir, Path.GetFileName(_copiedPath));
            try
            {
                if (Directory.Exists(_copiedPath))
                {
                    CopyDirectory(_copiedPath, dest);
                }
                else
                {
                    File.Copy(_copiedPath, dest, overwrite: true);
                }
                NavigateToDir(_currentDir);
                StatusBar.Text = $"Colado: {Path.GetFileName(_copiedPath)}";
            }
            catch (Exception ex)
            {
                StatusBar.Text = $"Erro ao colar: {ex.Message}";
            }
        }

        private void BtnDelete_Click(object _, RoutedEventArgs e)
        {
            var toDelete = FileList.SelectedItems.Cast<FileEntry>().ToList();
            if (toDelete.Count == 0) return;

            var result = MessageBox.Show($"Deletar {toDelete.Count} item(ns)?", "Confirmar",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            foreach (var fe in toDelete)
            {
                try
                {
                    if (fe.IsDirectory && Directory.Exists(fe.FullPath))
                        Directory.Delete(fe.FullPath, true);
                    else if (File.Exists(fe.FullPath))
                        File.Delete(fe.FullPath);
                }
                catch (Exception ex)
                {
                    StatusBar.Text = $"Erro ao deletar {fe.Name}: {ex.Message}";
                }
            }
            NavigateToDir(_currentDir);
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                File.Copy(file, Path.Combine(dest, name), true);
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                string name = Path.GetFileName(dir);
                CopyDirectory(dir, Path.Combine(dest, name));
            }
        }
    }
}
