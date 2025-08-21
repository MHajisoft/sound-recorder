using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SoundRecorder;

public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0d
        };

        return v < 60 ? Brushes.Green
            : v < 85 ? Brushes.Yellow
            : Brushes.Red;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}