using System.Globalization;

namespace LarsCloud.Infrastructure;

public static class Formatters
{
    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "—";
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString(value >= 100 || unit == 0 ? "0" : "0.0", CultureInfo.GetCultureInfo("uk-UA"))} {units[unit]}";
    }

    public static string DateTimeLocal(DateTimeOffset? value) =>
        value is null ? "Ще не виконувалась" : value.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    public static string Duration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours} год {value.Minutes} хв"
        : value.TotalMinutes >= 1 ? $"{(int)value.TotalMinutes} хв {value.Seconds} с" : $"{Math.Max(0, value.Seconds)} с";
}
