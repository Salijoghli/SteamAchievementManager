using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>Writes CSV files (UTF-8 with BOM so Excel opens them correctly, RFC 4180 quoting, invariant culture).</summary>
public static class CsvExport
{
    public static Task WriteLibraryAsync(string path, IEnumerable<LibraryExportRow> rows) =>
        throw new NotImplementedException("TODO(data-a)");

    public static Task WriteAchievementsAsync(string path, IEnumerable<AchievementExportRow> rows) =>
        throw new NotImplementedException("TODO(data-a)");
}
