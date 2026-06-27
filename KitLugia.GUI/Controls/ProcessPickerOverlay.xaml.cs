using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Resolve ambiguidades WPF vs WinForms
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Image = System.Windows.Controls.Image;

namespace KitLugia.GUI.Controls
{
    /// <summary>
    /// Overlay estilo Cheat Engine / Extreme Injector para selecionar processos.
    /// Lista todos os processos rodando, agrupa subprocessos, mostra ícones reais,
    /// e permite adicionar por arquivo .exe.
    /// </summary>
    public partial class ProcessPickerOverlay : UserControl
    {
        // Evento disparado quando o usuário confirma: (processName, limitMB)
        public event Action<string, long>? ProcessSelected;

        // Evento disparado quando o overlay é fechado sem confirmar
        public event Action? OverlayClosed;

        private List<ProcessEntry> _allEntries = new();
        private ProcessEntry? _selectedEntry;

        // Cor de destaque para item selecionado
        private static readonly SolidColorBrush _selectedBg =
            new(Color.FromArgb(40, 255, 215, 0));
        private static readonly SolidColorBrush _hoverBg =
            new(Color.FromArgb(20, 255, 255, 255));
        private static readonly SolidColorBrush _normalBg =
            new(Colors.Transparent);

        public ProcessPickerOverlay()
        {
            InitializeComponent();
        }

        // ───────────────────────────────────────────────────────────────

        public async void Open()
        {
            Visibility = Visibility.Visible;
            TxtSearch.Text = "";
            TxtSelectedProcess.Text = "Nenhum processo selecionado";
            BtnConfirm.IsEnabled = false;
            _selectedEntry = null;
            await RefreshAsync();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Visibility = Visibility.Collapsed;
            OverlayClosed?.Invoke();
        }

        // �"?�"?�"📋 Carregar processos �"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?

        private async Task RefreshAsync()
        {
            ProcessListPanel.Children.Clear();
            ProcessListPanel.Children.Add(new TextBlock
            {
                Text = "⏳ Carregando processos...",
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0)
            });

            _allEntries = await Task.Run(LoadProcesses);
            RenderList(_allEntries);
        }

        private static List<ProcessEntry> LoadProcesses()
        {
            var grouped = new Dictionary<string, ProcessEntry>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (proc.WorkingSet64 < 512 * 1024) { proc.Dispose(); continue; } // < 512KB
                        string name = proc.ProcessName.ToLowerInvariant();
                        if (name is "idle" or "system" or "registry") { proc.Dispose(); continue; }

                        long ramMB = proc.WorkingSet64 / (1024 * 1024);
                        string? exePath = null;
                        try { exePath = proc.MainModule?.FileName; } catch { }

                        if (grouped.TryGetValue(name, out var existing))
                        {
                            existing.TotalRamMB += ramMB;
                            existing.InstanceCount++;
                        }
                        else
                        {
                            grouped[name] = new ProcessEntry
                            {
                                ProcessName = name,
                                ExePath = exePath,
                                TotalRamMB = ramMB,
                                InstanceCount = 1,
                                Pid = proc.Id
                            };
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }

            return grouped.Values
                .OrderByDescending(e => e.TotalRamMB)
                .ToList();
        }

        // �"?�"?�"🖥️ Renderizar lista �"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?

        private void RenderList(List<ProcessEntry> entries)
        {
            ProcessListPanel.Children.Clear();

            if (entries.Count == 0)
            {
                ProcessListPanel.Children.Add(new TextBlock
                {
                    Text = "Nenhum processo encontrado.",
                    Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 20, 0, 0)
                });
                return;
            }

            foreach (var entry in entries)
            {
                var row = BuildProcessRow(entry);
                ProcessListPanel.Children.Add(row);
            }
        }

        private Border BuildProcessRow(ProcessEntry entry)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 1, 0, 1),
                Background = _normalBg,
                Cursor = Cursors.Hand,
                Tag = entry
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

            // Ícone
            var iconBorder = new Border
            {
                Width = 28, Height = 28,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(42, 42, 42)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var img = new System.Windows.Controls.Image
            {
                Width = 20, Height = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            // Carrega ícone em background para não travar a UI
            if (entry.ExePath != null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var icon = System.Drawing.Icon.ExtractAssociatedIcon(entry.ExePath);
                        if (icon != null)
                        {
                            var src = Imaging.CreateBitmapSourceFromHIcon(
                                icon.Handle, Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            src.Freeze();
                            icon.Dispose();
                            Dispatcher.Invoke(() => img.Source = src);
                        }
                    }
                    catch { }
                });
            }
            iconBorder.Child = img;
            Grid.SetColumn(iconBorder, 0);
            grid.Children.Add(iconBorder);

            // Nome + detalhes
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(new TextBlock
            {
                Text = entry.DisplayName,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13
            });

            // Badge de instâncias múltiplas
            if (entry.InstanceCount > 1)
            {
                nameRow.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(6, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = $"×{entry.InstanceCount}",
                        Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                        FontSize = 10
                    }
                });
            }
            nameStack.Children.Add(nameRow);

            // Caminho ou detalhes
            string detail = entry.ExePath != null
                ? Path.GetDirectoryName(entry.ExePath) ?? entry.ProcessName
                : entry.ProcessName + ".exe";
            nameStack.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });

            Grid.SetColumn(nameStack, 1);
            grid.Children.Add(nameStack);

            // RAM
            var ramColor = entry.TotalRamMB switch
            {
                > 2000 => Color.FromRgb(255, 85, 85),
                > 1000 => Color.FromRgb(255, 165, 0),
                > 500 => Color.FromRgb(255, 215, 0),
                _ => Color.FromRgb(136, 136, 136)
            };
            var ramText = new TextBlock
            {
                Text = $"{entry.TotalRamMB} MB",
                Foreground = new SolidColorBrush(ramColor),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(ramText, 2);
            grid.Children.Add(ramText);

            // Check mark (oculto por padrão)
            var check = new TextBlock
            {
                Text = "✓",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0)),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Tag = "check"
            };
            Grid.SetColumn(check, 3);
            grid.Children.Add(check);

            row.Child = grid;

            // Hover
            row.MouseEnter += (s, e) =>
            {
                if (row.Tag is ProcessEntry pe && pe != _selectedEntry)
                    row.Background = _hoverBg;
            };
            row.MouseLeave += (s, e) =>
            {
                if (row.Tag is ProcessEntry pe && pe != _selectedEntry)
                    row.Background = _normalBg;
            };

            // Click �?" seleciona
            row.MouseLeftButtonDown += (s, e) =>
            {
                if (row.Tag is ProcessEntry pe)
                    SelectEntry(pe, row);
            };

            // Double-click �?" confirma direto
            row.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2 && row.Tag is ProcessEntry pe)
                {
                    SelectEntry(pe, row);
                    BtnConfirm_Click(this, new RoutedEventArgs());
                }
            };

            entry.RowBorder = row;
            return row;
        }

        // �"?�"?�"✅ Seleção �"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?

        private void SelectEntry(ProcessEntry entry, Border row)
        {
            // Desmarca o anterior
            if (_selectedEntry?.RowBorder != null)
            {
                _selectedEntry.RowBorder.Background = _normalBg;
                _selectedEntry.RowBorder.BorderThickness = new Thickness(0);
                // Oculta check mark
                HideCheck(_selectedEntry.RowBorder);
            }

            _selectedEntry = entry;

            // Marca o novo
            row.Background = _selectedBg;
            row.BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 215, 0));
            row.BorderThickness = new Thickness(1);
            ShowCheck(row);

            TxtSelectedProcess.Text = $"Selecionado: {entry.DisplayName}  ({entry.TotalRamMB} MB em uso)";
            BtnConfirm.IsEnabled = true;

            // Sugere limite = 150% do uso atual, mínimo 200MB
            long suggested = entry.TotalRamMB > 0
                ? Math.Max(200, (long)(entry.TotalRamMB * 1.5))
                : 500;
            TxtLimitMB.Text = suggested.ToString();
        }

        private static void ShowCheck(Border row)
        {
            foreach (var child in GetAllChildren(row))
                if (child is TextBlock tb && tb.Tag?.ToString() == "check")
                    tb.Visibility = Visibility.Visible;
        }

        private static void HideCheck(Border row)
        {
            foreach (var child in GetAllChildren(row))
                if (child is TextBlock tb && tb.Tag?.ToString() == "check")
                    tb.Visibility = Visibility.Collapsed;
        }

        private static IEnumerable<DependencyObject> GetAllChildren(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var grandchild in GetAllChildren(child))
                    yield return grandchild;
            }
        }

        // �"?�"?�"🎯 Handlers �"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = TxtSearch.Text.Trim();
            if (string.IsNullOrEmpty(q))
            {
                RenderList(_allEntries);
            }
            else
            {
                var filtered = _allEntries
                    .Where(p => p.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)
                             || p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                RenderList(filtered);
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            _selectedEntry = null;
            TxtSelectedProcess.Text = "Nenhum processo selecionado";
            BtnConfirm.IsEnabled = false;
            await RefreshAsync();
        }

        /// <summary>
        /// Abre o File Explorer para o usuário selecionar um .exe manualmente.
        /// Não precisa que o processo esteja rodando.
        /// </summary>
        private void BtnBrowseExe_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Selecione o executável do processo",
                Filter = "Executáveis (*.exe)|*.exe",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };

            if (dialog.ShowDialog() != true) return;

            string exePath = dialog.FileName;
            string processName = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();

            // Verifica se já existe na lista
            var existing = _allEntries.FirstOrDefault(
                e => e.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                // Já existe �?" só seleciona
                if (existing.RowBorder != null)
                    SelectEntry(existing, existing.RowBorder);
            }
            else
            {
                // Cria entrada manual
                var entry = new ProcessEntry
                {
                    ProcessName = processName,
                    ExePath = exePath,
                    TotalRamMB = 0,
                    InstanceCount = 0, // Não está rodando agora
                    Pid = -1
                };
                _allEntries.Insert(0, entry);
                RenderList(_allEntries);

                // Seleciona o recém-adicionado
                if (entry.RowBorder != null)
                    SelectEntry(entry, entry.RowBorder);
            }
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedEntry == null) return;

            if (!long.TryParse(TxtLimitMB.Text?.Trim(), out long limitMB) || limitMB < 50)
            {
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowError("❌ Limite inválido", "Digite um valor em MB (mínimo 50).");
                return;
            }

            ProcessSelected?.Invoke(_selectedEntry.ProcessName, limitMB);
            Visibility = Visibility.Collapsed;
        }
    }

    // �"?�"?�"📊 Modelo de dados �"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?�"?

    public class ProcessEntry
    {
        public string ProcessName { get; set; } = "";
        public string? ExePath { get; set; }
        public long TotalRamMB { get; set; }
        public int InstanceCount { get; set; }
        public int Pid { get; set; }

        // Referência ao Border na UI para poder atualizar o visual
        public Border? RowBorder { get; set; }

        public string DisplayName
        {
            get
            {
                if (ExePath != null && File.Exists(ExePath))
                {
                    try
                    {
                        var info = FileVersionInfo.GetVersionInfo(ExePath);
                        if (!string.IsNullOrEmpty(info.ProductName) &&
                            !info.ProductName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
                            return info.ProductName;
                        if (!string.IsNullOrEmpty(info.FileDescription))
                            return info.FileDescription;
                    }
                    catch { }
                }
                // Capitaliza
                return ProcessName.Length > 0
                    ? char.ToUpper(ProcessName[0]) + ProcessName.Substring(1)
                    : ProcessName;
            }
        }
    }
}
