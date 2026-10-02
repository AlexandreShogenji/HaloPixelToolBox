using Microsoft.UI.Xaml.Data;

namespace HaloPixelToolBox.Utilities.Converter;

public partial class BooleanInverseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not bool boolValue)
            return value;

        if (targetType == typeof(Visibility))
            return boolValue ? Visibility.Collapsed : Visibility.Visible;

        return !boolValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
            return !boolValue;

        if (value is Visibility visibility)
            return visibility != Visibility.Visible;

        return value;
    }
}
