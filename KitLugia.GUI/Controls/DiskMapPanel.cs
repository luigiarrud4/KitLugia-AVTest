using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;
using WColor = System.Windows.Media.Color;
using WBrushes = System.Windows.Media.Brushes;
using WHoriz = System.Windows.HorizontalAlignment;
using WFontFamily = System.Windows.Media.FontFamily;

namespace KitLugia.GUI.Controls
{
    /// <summary>
    /// Mapa visual de disco + partições (barra proporcional, estilo Gerenciador de Discos).
    /// Usado nas páginas Reparar Boot/BCD e Conversor MBR/GPT para o usuário VER o disco
    /// de forma direta (letra, tamanho, sistema de arquivos, ESP, recuperação, livre).
    /// Dados: DiskConverterManager (leitura pura, sem alterar nada).
    /// </summary>
    public static class DiskMapPanel
    {
        private static WColor C(byte r, byte g, byte b) => WColor.FromRgb(r, g, b);

        private static WColor ColorFor(PartitionRow p, bool highlight)
        {
            string t = (p.GptType ?? "").ToUpperInvariant();
            string letter = (p.DriveLetter ?? "").ToUpperInvariant();
            // ESP (EFI System): dourado | MSR/Recovery: roxo | boot/C:: azul forte | dados: azul | sem letra: cinza
            if (t.Contains("C12A7328") || p.IsSystem) return C(212, 175, 55);
            if (t.Contains("E3C9E316") || t.Contains("DE94BBA4")) return C(126, 87, 194);
            if (letter == "C") return C(58, 134, 255);
            if (!string.IsNullOrEmpty(p.DriveLetter)) return C(79, 195, 247);
            if (p.IsBoot || p.IsActive) return C(58, 134, 255);
            return C(90, 90, 90);
        }

        private static string LabelFor(PartitionRow p)
        {
            string t = (p.GptType ?? "").ToUpperInvariant();
            if (t.Contains("C12A7328") || p.IsSystem) return "EFI";
            if (t.Contains("E3C9E316")) return "MSR";
            if (t.Contains("DE94BBA4")) return "Recuperação";
            if (!string.IsNullOrEmpty(p.DriveLetter)) return p.DriveLetter + ":";
            return "—";
        }

        /// <summary>
        /// Monta o mapa do disco (barra + legenda). highlightLetters: letras a destacar
        /// com borda dourada (ex.: Windows + ESP na página do BCD).
        /// </summary>
        public static StackPanel Build(uint diskNumber, HashSet<string>? highlightLetters = null)
        {
            var root = new StackPanel();
            try
            {
                var disk = DiskConverterManager.GetDisks()
                    .FirstOrDefault(d => d.Number == diskNumber);
                if (disk == null)
                {
                    root.Children.Add(new TextBlock
                    {
                        Text = $"Disco {diskNumber} não encontrado.",
                        Foreground = new SolidColorBrush(C(150, 150, 150)), FontSize = 11
                    });
                    return root;
                }
                var parts = DiskConverterManager.GetPartitions(diskNumber)
                    .OrderBy(p => p.Offset).ToList();
                highlightLetters ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var bar = new Grid { Height = 44, Margin = new Thickness(0, 0, 0, 8) };
                ulong total = disk.Size > 0 ? disk.Size : parts.Aggregate(0UL, (a, p) => a + p.Size);
                if (total == 0) total = 1;
                ulong cursor = 0;
                int col = 0;
                foreach (var p in parts)
                {
                    // Buraco (não alocado) entre partições.
                    if (p.Offset > cursor)
                    {
                        AddBlock(bar, total, p.Offset - cursor, ref col,
                            C(30, 30, 30), "Livre", "", false, dashed: true);
                    }
                    bool hl = !string.IsNullOrEmpty(p.DriveLetter) && highlightLetters.Contains(p.DriveLetter);
                    string sub = p.SizeText + (p.IsBoot ? " • boot" : "");
                    AddBlock(bar, total, p.Size, ref col,
                        ColorFor(p, hl), LabelFor(p), sub, hl, dashed: false);
                    cursor = p.Offset + p.Size;
                }
                if (cursor < total)
                    AddBlock(bar, total, total - cursor, ref col, C(30, 30, 30), "Livre", "", false, dashed: true);
                root.Children.Add(bar);

                // Legenda: uma linha por partição (rótulo, tamanho, flags).
                foreach (var p in parts)
                {
                    string flags = "";
                    if (p.IsSystem) flags += " [SISTEMA]";
                    if (p.IsBoot) flags += " [BOOT]";
                    if (p.IsActive) flags += " [ATIVA]";
                    if (p.IsReadOnly) flags += " [SOMENTE LEITURA]";
                    root.Children.Add(new TextBlock
                    {
                        Text = $"{LabelFor(p),-12} {p.SizeText,10}{flags}",
                        FontFamily = new WFontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(C(200, 200, 200)),
                        Margin = new Thickness(2, 1, 0, 0)
                    });
                }
                root.Children.Add(new TextBlock
                {
                    Text = $"Disco {disk.Number} • {disk.Model} • {disk.SizeText} • {disk.StyleText}" +
                           (disk.IsSystem ? " • DISCO DO SISTEMA" : ""),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(C(140, 140, 140)),
                    Margin = new Thickness(2, 6, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            catch (Exception ex)
            {
                root.Children.Add(new TextBlock
                {
                    Text = "Não foi possível ler o disco: " + ex.Message,
                    Foreground = new SolidColorBrush(C(255, 120, 120)), FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            return root;
        }

        private static void AddBlock(Grid bar, ulong total, ulong size, ref int col,
            WColor color, string label, string sub, bool highlight, bool dashed)
        {
            if (size == 0) return;
            bar.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(1, (double)size / total * 1000), GridUnitType.Star)
            });
            var border = new Border
            {
                Background = new SolidColorBrush(color),
                BorderBrush = highlight
                    ? new SolidColorBrush(C(255, 215, 0))
                    : new SolidColorBrush(WColor.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(highlight ? 2 : 1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(1, 0, 1, 0),
                ToolTip = string.IsNullOrEmpty(sub) ? label : $"{label} — {sub}"
            };
            if (dashed)
                border.BorderBrush = new SolidColorBrush(C(80, 80, 80));
            double share = (double)size / total;
            if (share > 0.03)
            {
                var stack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = WHoriz.Center
                };
                stack.Children.Add(new TextBlock
                {
                    Text = label, FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = WBrushes.White, HorizontalAlignment = WHoriz.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                if (share > 0.07 && !string.IsNullOrEmpty(sub))
                    stack.Children.Add(new TextBlock
                    {
                        Text = sub, FontSize = 9, Opacity = 0.85,
                        Foreground = WBrushes.White, HorizontalAlignment = WHoriz.Center
                    });
                border.Child = stack;
            }
            Grid.SetColumn(border, col++);
            bar.Children.Add(border);
        }
    }
}
