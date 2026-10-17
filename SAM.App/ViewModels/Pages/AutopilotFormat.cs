using System.Globalization;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SAM.App.Models;

namespace SAM.App.ViewModels.Pages;

/// <summary>Text, colour and copy helpers for the Autopilot page and its dialogs (also used by x:Bind).</summary>
public static class AutopilotFormat
{
    private static readonly JsonSerializerOptions CopyOptions = new();

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    /// <summary>Full opacity when enabled, dimmed otherwise.</summary>
    public static double DimUnless(bool enabled) => enabled ? 1 : 0.5;

    /// <summary>A deep copy of a configuration (the page edits a copy and saves it as a whole).</summary>
    public static AutopilotConfig Clone(AutopilotConfig config) =>
        JsonSerializer.Deserialize<AutopilotConfig>(JsonSerializer.Serialize(config, CopyOptions), CopyOptions) ?? new AutopilotConfig();

    /// <summary>A deep copy of a per-game override.</summary>
    public static AutopilotGameOverride Clone(AutopilotGameOverride value) =>
        JsonSerializer.Deserialize<AutopilotGameOverride>(JsonSerializer.Serialize(value, CopyOptions), CopyOptions) ?? new AutopilotGameOverride { AppId = value.AppId };

    /// <summary>A comparable snapshot of a configuration, for "has the user changed anything".</summary>
    public static string Snapshot(AutopilotConfig config) => JsonSerializer.Serialize(config, CopyOptions);

    /// <summary>"12.3%", "0.42%", or "–" when unknown.</summary>
    public static string Percent(double? percent) => percent switch
    {
        null => "–",
        < 1 => percent.Value.ToString("0.##", CultureInfo.CurrentCulture) + "%",
        _ => percent.Value.ToString("0.#", CultureInfo.CurrentCulture) + "%",
    };

    /// <summary>"5.5 h" (one decimal below 10 hours, whole hours above).</summary>
    public static string Hours(double hours) =>
        Strings.Format("LibraryFormat_Hours", hours.ToString(hours < 10 ? "0.#" : "N0", CultureInfo.CurrentCulture));

    /// <summary>"in 12 min", "in 3 h", "in 2 days", "any moment now" — or "" when unknown.</summary>
    public static string RelativeEta(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } value)
        {
            return "";
        }

        var span = value - now;
        return span.TotalMinutes switch
        {
            < 1 => Strings.Get("Autopilot_EtaNow"),
            < 60 => Strings.Plural("Autopilot_EtaMinutes", (long)Math.Ceiling(span.TotalMinutes)),
            < 48 * 60 => Strings.Plural("Autopilot_EtaHours", (long)Math.Round(span.TotalHours)),
            _ => Strings.Plural("Autopilot_EtaDays", (long)Math.Round(span.TotalDays)),
        };
    }

    public static string DateTime(DateTimeOffset? at) =>
        at is { } value ? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "–";

    public static string StatusText(AutopilotGameStatusKind kind) => Strings.Get("Autopilot_Status" + kind);

    /// <summary>Segoe Fluent glyph per state, so the pill never relies on colour alone.</summary>
    public static string StatusGlyph(AutopilotGameStatusKind kind) => kind switch
    {
        AutopilotGameStatusKind.Queued => "",
        AutopilotGameStatusKind.Starting => "",
        AutopilotGameStatusKind.Running => "",
        AutopilotGameStatusKind.WaitingForSchedule => "",
        AutopilotGameStatusKind.PausedWhileGaming => "",
        AutopilotGameStatusKind.PausedByUser => "",
        AutopilotGameStatusKind.DailyLimitReached => "",
        AutopilotGameStatusKind.Completed => "",
        _ => "",
    };

    /// <summary>
    /// Pill colour per state. Mid-tone fills with white text (like the rarity pills), so they read
    /// the same in light and dark themes: green running, blue starting, amber waiting/limited,
    /// grey queued/paused, teal completed, red error.
    /// </summary>
    public static Brush StatusBrush(AutopilotGameStatusKind kind) => new SolidColorBrush(kind switch
    {
        AutopilotGameStatusKind.Running => ColorHelper.FromArgb(255, 0x2E, 0x7D, 0x32),
        AutopilotGameStatusKind.Starting => ColorHelper.FromArgb(255, 0x1F, 0x6F, 0xD1),
        AutopilotGameStatusKind.WaitingForSchedule or
        AutopilotGameStatusKind.PausedWhileGaming or
        AutopilotGameStatusKind.DailyLimitReached => ColorHelper.FromArgb(255, 0x9A, 0x58, 0x00),
        AutopilotGameStatusKind.Completed => ColorHelper.FromArgb(255, 0x00, 0x6E, 0x63),
        AutopilotGameStatusKind.Error => ColorHelper.FromArgb(255, 0xC0, 0x29, 0x29),
        _ => ColorHelper.FromArgb(255, 0x5F, 0x66, 0x70),
    });

    public static string SkipReasonText(AutopilotSkipReason reason) => Strings.Get("Autopilot_Reason" + reason);

    /// <summary>A game's display name from the library, Steam's app data, or "App 123".</summary>
    public static string GameName(LibraryViewModel library, Services.AppServices services, uint appId)
    {
        foreach (var card in library.AllGames)
        {
            if (card.AppId == appId)
            {
                return card.Name;
            }
        }

        try
        {
            if (services.Steam.GetAppData(appId, "name") is { Length: > 0 } name)
            {
                return name;
            }
        }
        catch (Exception e)
        {
            Services.Diagnostics.Log($"Couldn't read the name of app {appId}", e);
        }

        return Strings.Format("Pages_AppFallbackName", appId);
    }

    /// <summary>The message shown when a call into Autopilot fails (the engine may not be available yet).</summary>
    public static string ErrorMessage(Exception e) => e is NotImplementedException
        ? Strings.Get("Autopilot_EngineUnavailable")
        : Strings.Format("Autopilot_ErrorDetail", e.Message);
}
