using System.Globalization;
using System.Text;
using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// Writes CSV files (UTF-8 with BOM so Excel opens them correctly, RFC 4180 quoting, CRLF line
/// endings, invariant culture, ISO 8601 local times). Files are written to a temporary file first
/// and then moved into place, so a failed export never leaves a half-written file behind.
/// </summary>
public static class CsvExport
{
    // Column headers are in the UI language (the files are for reading in a spreadsheet); the
    // values stay culture-invariant.
    private static string[] LibraryHeader =>
    [
        Strings.Get("Export_ColumnAppId"), Strings.Get("Export_ColumnName"), Strings.Get("Export_ColumnType"),
        Strings.Get("Export_ColumnAchievementsUnlocked"), Strings.Get("Export_ColumnAchievementsTotal"),
        Strings.Get("Export_ColumnCompletion"), Strings.Get("Export_ColumnPlaytimeHours"), Strings.Get("Export_ColumnLastPlayed"),
        Strings.Get("Export_ColumnInstalled"), Strings.Get("Export_ColumnFavorite"),
    ];

    private static string[] AchievementsHeader =>
    [
        Strings.Get("Export_ColumnAppId"), Strings.Get("Export_ColumnGame"), Strings.Get("Export_ColumnAchievementId"),
        Strings.Get("Export_ColumnName"), Strings.Get("Export_ColumnDescription"), Strings.Get("Export_ColumnUnlocked"),
        Strings.Get("Export_ColumnUnlockTime"), Strings.Get("Export_ColumnGlobalPercent"),
    ];

    /// <summary>Writes one row per game to <paramref name="path"/>, replacing it if it exists.</summary>
    public static Task WriteLibraryAsync(string path, IEnumerable<LibraryExportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return WriteAsync(path, LibraryHeader, rows.Select(r => new[]
        {
            Format(r.AppId),
            r.Name,
            r.Type,
            Format(r.Unlocked),
            Format(r.Total),
            r.Unlocked is { } u && r.Total is > 0 and var t ? Format(100.0 * u / t) : "",
            Format(r.PlaytimeHours),
            Format(r.LastPlayed),
            Format(r.Installed),
            Format(r.Favorite),
        }));
    }

    /// <summary>Writes one row per achievement to <paramref name="path"/>, replacing it if it exists.</summary>
    public static Task WriteAchievementsAsync(string path, IEnumerable<AchievementExportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return WriteAsync(path, AchievementsHeader, rows.Select(r => new[]
        {
            Format(r.AppId),
            r.GameName,
            r.AchievementId,
            r.Name,
            r.Description,
            Format(r.Unlocked),
            Format(r.UnlockTime),
            Format(r.GlobalPercent),
        }));
    }

    /// <summary>Quotes <paramref name="field"/> per RFC 4180 if it contains a comma, quote, CR or LF.</summary>
    internal static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return "";
        }

        return field.AsSpan().IndexOfAny(",\"\r\n") < 0
            ? field
            : "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static async Task WriteAsync(string path, string[] header, IEnumerable<string?[]> rows)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            await using (var writer = new StreamWriter(temp, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.NewLine = "\r\n";
                await WriteRowAsync(writer, header).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    await WriteRowAsync(writer, row).ConfigureAwait(false);
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    private static Task WriteRowAsync(StreamWriter writer, string?[] fields) =>
        writer.WriteLineAsync(string.Join(",", fields.Select(Escape)));

    private static string Format(uint value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Format(double? value) =>
        value is { } v && double.IsFinite(v) ? v.ToString("0.##", CultureInfo.InvariantCulture) : "";

    private static string Format(bool value) => value ? "Yes" : "No";

    private static string Format(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) ?? "";
}
