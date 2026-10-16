namespace SAM.App.Services;

/// <summary>Builds Steam CDN URLs for game art and achievement icons.</summary>
public sealed class GameArt(SteamSession steam)
{
    private const string StoreAssets = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps";

    /// <summary>
    /// Candidate URLs for a game's card image, best first: the store header image (460x215)
    /// at the path Steam's app data gives (newer games use a hashed folder; the plain
    /// header.jpg path serves a grey placeholder for them), then the plain header.jpg, then
    /// the localized/English small capsule and legacy logo.
    /// </summary>
    public IReadOnlyList<string> GetCardImageUrls(uint appId)
    {
        var urls = new List<string>(6);

        try
        {
            var language = steam.Language;
            var header = steam.GetAppData(appId, $"header_image/{language}") ??
                         steam.GetAppData(appId, "header_image/english");
            if (header != null)
            {
                urls.Add($"{StoreAssets}/{appId}/{header}");
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't read header image app data for {appId}", e);
        }

        urls.Add($"{StoreAssets}/{appId}/header.jpg");

        try
        {
            var language = steam.Language;
            var capsule = steam.GetAppData(appId, $"small_capsule/{language}");
            if (capsule != null)
            {
                urls.Add($"{StoreAssets}/{appId}/{capsule}");
            }

            if (language != "english")
            {
                capsule = steam.GetAppData(appId, "small_capsule/english");
                if (capsule != null)
                {
                    urls.Add($"{StoreAssets}/{appId}/{capsule}");
                }
            }

            var logo = steam.GetAppData(appId, "logo");
            if (logo != null)
            {
                urls.Add($"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/{logo}.jpg");
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't read image app data for {appId}", e);
        }

        return urls.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>URL of an achievement icon file named in the schema.</summary>
    public static string GetAchievementIconUrl(uint appId, string iconFile) =>
        $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/{iconFile}";
}
