using System.Globalization;
using Microsoft.UI.Xaml;
using SAM.App.Models;

namespace SAM.App.ViewModels;

/// <summary>Text formatting and x:Bind helper functions for the library window.</summary>
public static class LibraryFormat
{
    public static string Playtime(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return "0 h";
        }

        if (time.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)time.TotalMinutes)} min";
        }

        var hours = time.TotalHours;
        return hours >= 1000
            ? $"{hours.ToString("N0", CultureInfo.CurrentCulture)} h"
            : $"{hours.ToString("0.#", CultureInfo.CurrentCulture)} h";
    }

    public static string RelativeDate(DateTimeOffset? value)
    {
        if (value == null)
        {
            return "Never played";
        }

        var days = (DateTime.Now.Date - value.Value.LocalDateTime.Date).Days;
        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 7 => $"{days} days ago",
            < 14 => "1 week ago",
            < 30 => $"{days / 7} weeks ago",
            < 60 => "1 month ago",
            < 365 => $"{days / 30} months ago",
            < 730 => "1 year ago",
            _ => $"{days / 365} years ago",
        };
    }

    public static string PlaytimeSummary(PlaytimeInfo? info)
    {
        if (info is not { } p || (p.LastPlayed == null && p.Playtime <= TimeSpan.Zero))
        {
            return "Never played";
        }

        return p.LastPlayed == null
            ? Playtime(p.Playtime)
            : $"{Playtime(p.Playtime)} · {RelativeDate(p.LastPlayed)}";
    }

    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes"
            : $"{value.ToString("0.#", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    public static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    // x:Bind helpers.
    public static bool Not(bool value) => !value;

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAll(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfNotNull(object? value) => value != null ? Visibility.Visible : Visibility.Collapsed;

    public static double OpacityIf(bool value) => value ? 1 : 0;
}
