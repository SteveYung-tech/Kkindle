using Microsoft.UI.Xaml.Data;

namespace Kkindle;

public sealed class ReaderProgressToolTipValueConverter : IValueConverter
{
    private readonly Func<int, string> _format;

    public ReaderProgressToolTipValueConverter(Func<int, string> format)
    {
        _format = format;
    }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var numericValue = value is double number ? number : System.Convert.ToDouble(value);
        return _format((int)Math.Round(numericValue));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
