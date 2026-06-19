using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KitLugia.GUI.Converters
{
    public class StarWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double d)
            {
                // Garante que o GridLength seja proporcional (Star)
                // Se for 0, coloca um valor mínimo para não quebrar o layout
                return new GridLength(Math.Max(0.01, d), GridUnitType.Star);
            }
            if (value is float f)
            {
                return new GridLength(Math.Max(0.01, f), GridUnitType.Star);
            }
            return new GridLength(1, GridUnitType.Star);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
