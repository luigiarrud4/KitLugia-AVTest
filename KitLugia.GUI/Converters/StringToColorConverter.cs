using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace KitLugia.GUI.Converters
{
    public class StringToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hex && !string.IsNullOrEmpty(hex))
            {
                try
                {
                    return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                }
                catch
                {
                    return Colors.Gray;
                }
            }
            return Colors.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
