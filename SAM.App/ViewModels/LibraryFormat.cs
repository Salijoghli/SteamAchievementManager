using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SAM.App.Models;

namespace SAM.App.ViewModels;

/// <summary>Text formatting and x:Bind helper functions for the library window.</summary>
public static class LibraryFormat
{
    public static string Playtime(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return Strings.Format("LibraryFormat_Hours", 0.ToString(CultureInfo.CurrentCulture));
        }

        if (time.TotalHours < 1)
        {
            return Strings.Format("LibraryFormat_Minutes", Math.Max(1, (int)time.TotalMinutes).ToString(CultureInfo.CurrentCulture));
        }

        var hours = time.TotalHours;
        return Strings.Format(
            "LibraryFormat_Hours",
            hours >= 1000 ? hours.ToString("N0", CultureInfo.CurrentCulture) : hours.ToString("0.#", CultureInfo.CurrentCulture));
    }

    public static string RelativeDate(DateTimeOffset? value)
    {
        if (value == null)
        {
            return Strings.Get("LibraryFormat_NeverPlayed");
        }

        var days = (DateTime.Now.Date - value.Value.LocalDateTime.Date).Days;
        return days switch
        {
            <= 0 => Strings.Get("LibraryFormat_Today"),
            1 => Strings.Get("LibraryFormat_Yesterday"),
            < 7 => Strings.Plural("LibraryFormat_DaysAgo", days),
            < 30 => Strings.Plural("LibraryFormat_WeeksAgo", days / 7),
            < 365 => Strings.Plural("LibraryFormat_MonthsAgo", Math.Max(1, days / 30)),
            _ => Strings.Plural("LibraryFormat_YearsAgo", days / 365),
        };
    }

    public static string PlaytimeSummary(PlaytimeInfo? info)
    {
        if (info is not { } p || (p.LastPlayed == null && p.Playtime <= TimeSpan.Zero))
        {
            return Strings.Get("LibraryFormat_NeverPlayed");
        }

        return p.LastPlayed == null
            ? Playtime(p.Playtime)
            : Strings.Format("LibraryFormat_PlaytimeAndLastPlayed", Playtime(p.Playtime), RelativeDate(p.LastPlayed));
    }

    public static string Bytes(long bytes)
    {
        string[] units = ["", "LibraryFormat_KB", "LibraryFormat_MB", "LibraryFormat_GB", "LibraryFormat_TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? Strings.Plural("LibraryFormat_Bytes", bytes)
            : Strings.Format(units[unit], value.ToString("0.#", CultureInfo.CurrentCulture));
    }

    public static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    // x:Bind helpers.
    public static bool Not(bool value) => !value;

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAll(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfNotNull(object? value) => value != null ? Visibility.Visible : Visibility.Collapsed;

    public static double OpacityIf(bool value) => value ? 1 : 0;

    /// <summary>Sort arrow for a list view column header: shown only on the active column.</summary>
    public static string SortGlyph(string activeColumn, bool descending, string column) =>
        activeColumn != column ? "" : descending ? "" : "";

    /// <summary>The list view's Favorite header: a star, or the sort arrow while sorted by it.</summary>
    public static string FavoriteHeaderGlyph(string activeColumn, bool descending) =>
        activeColumn == LibraryViewModel.FavoriteColumn ? SortGlyph(activeColumn, descending, activeColumn) : "";

    /// <summary>A small decoded thumbnail of a cached card image for the list view, or null.</summary>
    public static ImageSource? Thumbnail(string? path) => path == null
        ? null
        : new BitmapImage { DecodePixelWidth = 96, DecodePixelType = DecodePixelType.Logical, UriSource = new Uri(path) };
}
