using System;
using System.Globalization;
using System.Windows.Data;

namespace KitLugia.GUI.Converters
{
    /// <summary>
    /// Converte uma string hexadecimal de cor (#RRGGBB) em SolidColorBrush.
    /// Usado para aplicar as cores de status calculadas no ScannableTweak
    /// (StatusTextColor / StatusBadgeBackground), que levam em conta
    /// IsOptional para nunca exibir "PROTEGIDO" em itens opcionais.
    /// </summary>
    public sealed class HexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hex && !string.IsNullOrEmpty(hex))
            {
                try
                {
                    var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                    return new System.Windows.Media.SolidColorBrush(color);
                }
                catch { return System.Windows.Media.Brushes.Gray; }
            }
            return System.Windows.Media.Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
