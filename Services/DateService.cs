using System.Globalization;

namespace SoundRecorder.Services;

public static class DateService
{
    private static readonly PersianCalendar PersianCalendar = new();
    
    public static string GetPersianDate(DateTime date)
    {
        return $"{PersianCalendar.GetYear(date)}-{PersianCalendar.GetMonth(date):D2}-{PersianCalendar.GetDayOfMonth(date):D2}";
    }
    
    public static uint GetPersianYear(DateTime date)
    {
        return (uint)PersianCalendar.GetYear(date);
    }
}
