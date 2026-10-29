using System.Globalization;
using SAM.App.Models;

namespace SAM.App.ViewModels;

/// <summary>Progress toward a counter achievement, e.g. 340 of 1,000 kills.</summary>
/// <param name="Percent">How far along, 0–100, measured from the schema's min to its max.</param>
/// <param name="Text">"340 / 1,000" in the current culture.</param>
/// <param name="AccessibleText">"Progress: 340 of 1,000", for screen readers.</param>
public sealed record AchievementProgressInfo(double Percent, string Text, string AccessibleText);

/// <summary>
/// Turns an achievement's progress stat (the schema's progress block) and the stat's current
/// value into the text and fraction shown under a locked achievement. Read-only: the stat
/// value is what Steam reported at the last load.
/// </summary>
public static class GameAchievementProgress
{
    /// <summary>
    /// The progress for <paramref name="definition"/> given its stat's current value, or null when
    /// the achievement has no usable progress block, or the stat's value is unknown.
    /// </summary>
    /// <param name="isIntegerStat">True for integer stats (shown without decimals).</param>
    public static AchievementProgressInfo? Compute(
        AchievementDefinition definition,
        double? statValue,
        bool isIntegerStat,
        CultureInfo? culture = null)
    {
        if (string.IsNullOrEmpty(definition.ProgressStatId) ||
            definition.ProgressMax is not { } max ||
            statValue is not { } value ||
            double.IsFinite(max) == false ||
            double.IsFinite(value) == false)
        {
            return null;
        }

        var min = definition.ProgressMin is { } m && double.IsFinite(m) ? m : 0;

        // An integer 0–1 range is a done/not-done flag (Battlefield 2042 has one per achievement): "0 / 1" says nothing.
        if (max <= min || (isIntegerStat && max - min <= 1))
        {
            return null;
        }

        culture ??= CultureInfo.CurrentCulture;
        var shown = Math.Clamp(value, Math.Min(0, min), max);
        var percent = Math.Clamp((value - min) / (max - min), 0, 1) * 100;

        var decimals = isIntegerStat || (IsWhole(max) && IsWhole(shown)) ? 0 : DecimalsFor(max);

        // Rounded down so a locked achievement never reads "1,000 / 1,000" at 999.7.
        var current = Truncate(shown, decimals);
        var format = "N" + decimals.ToString(CultureInfo.InvariantCulture);
        var currentText = current.ToString(format, culture);
        var maxText = max.ToString(IsWhole(max) ? "N0" : format, culture);
        return new AchievementProgressInfo(
            percent,
            Strings.Format("Game_ProgressText", currentText, maxText),
            Strings.Format("Game_ProgressA11y", currentText, maxText));
    }

    /// <summary>Decimals for a float stat: none from 100 up, one from 1, two below that.</summary>
    internal static int DecimalsFor(double max) => Math.Abs(max) switch
    {
        >= 100 => 0,
        >= 1 => 1,
        _ => 2,
    };

    private static bool IsWhole(double value) => Math.Abs(value - Math.Round(value)) < 1e-9;

    private static double Truncate(double value, int decimals)
    {
        var scale = Math.Pow(10, decimals);
        var scaled = value * scale;

        // Absorbs float noise (e.g. 0.3 stored as 0.29999998) before rounding down.
        var nearest = Math.Round(scaled);
        return (Math.Abs(scaled - nearest) < 1e-4 ? nearest : Math.Floor(scaled)) / scale;
    }
}
