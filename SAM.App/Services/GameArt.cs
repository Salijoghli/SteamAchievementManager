namespace SAM.App.Services;

/// <summary>Builds Steam CDN URLs for game art and achievement icons.</summary>
public sealed class GameArt(SteamSession steam)
{
    /// <summary>
    /// Candidate URLs for a game's card image, best first: the store header image
    /// (460x215), then the localized/English small capsule and legacy logo from Steam's app data.
    /// </summary>
    public IReadOnlyList<string> GetCardImageUrls(uint appId)
    {
        var urls = new List<string>(4)
        {
            $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
        };

        try
        {
            var language = steam.Language;
            var capsule = steam.GetAppData(appId, $"small_capsule/{language}");
            if (capsule != null)
            {
                urls.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/{capsule}");
            }

            if (language != "english")
            {
                capsule = steam.GetAppData(appId, "small_capsule/english");
                if (capsule != null)
                {
                    urls.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/{capsule}");
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
