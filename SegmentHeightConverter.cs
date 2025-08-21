using System.Globalization;
using System.Windows.Data;

namespace SoundRecorder;

public sealed class SegmentHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: >= 3 }) return 0d;

        var value = ToDouble(values[0]);
        var maximum = ToDouble(values[1]);
        var actualHeight = ToDouble(values[2]);
        var segment = parameter as string ?? "";

        if (maximum <= 0 || actualHeight <= 0) return segment.Contains("Margin") ? new System.Windows.Thickness(0) : 0d;

        // Thresholds as fractions of maximum
        var t1 = 0.50 * maximum; // green up to t1
        var t2 = 0.85 * maximum; // orange up to t2, red beyond

        // Compute absolute pixel heights for each segment
        var greenHeight = actualHeight * (Math.Min(value, t1) / maximum);
        var orangeHeight = actualHeight * (Clamp(Math.Min(value, t2) - t1, 0, t2 - t1) / maximum);
        var redHeight = actualHeight * (Clamp(value - t2, 0, maximum - t2) / maximum);

        return segment switch
        {
            // Heights
            "Green" => greenHeight,
            "Orange" => orangeHeight,
            "Red" => redHeight,

            // Margins (Bottom offsets to stack without overlap)
            "OrangeMargin" => new System.Windows.Thickness(0, 0, 0, greenHeight),
            "RedMargin" => new System.Windows.Thickness(0, 0, 0, greenHeight + orangeHeight),
            _ => 0d
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static double ToDouble(object o) => o switch
    {
        double d => d,
        float f => f,
        int i => i,
        decimal m => (double)m,
        _ => 0d
    };

    private static double Clamp(double v, double min, double max)
    {
        return v < min ? min : v > max ? max : v;
    }
}