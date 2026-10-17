using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>
/// Installed games: parses steamapps\libraryfolders.vdf for every Steam library folder, then
/// each folder's appmanifest_&lt;appid&gt;.acf. Thread-safe; safe to call off the UI thread.
/// </summary>
public sealed class InstalledGamesService(SteamSession steam)
{
    public IReadOnlyDictionary<uint, InstalledGame> Load() => throw new NotImplementedException("TODO(data-b)");
}
