using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SoundRecorder;

public class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var v = value switch
            {
                double d => d,
                float f => f,
                int i => i,
                _ => 0
            };
            return v switch
            {
                < 60 => Brushes.Green,
                < 85 => Brushes.Yellow,
                _ => Brushes.Red
            };
        }
        catch
        {
            return Brushes.Green;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}