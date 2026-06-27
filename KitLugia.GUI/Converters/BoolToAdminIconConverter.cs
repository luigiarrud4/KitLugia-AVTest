using System;
using System.Globalization;
using System.Windows.Data;

namespace KitLugia.GUI.Converters
{
    public class BoolToAdminIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isAdmin && isAdmin) return "🔒";
            return "🔓";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
