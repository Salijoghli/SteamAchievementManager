using System.Collections.Concurrent;
using System.Globalization;
using SAM.App.Models;

namespace SAM.App.Services;

/// <summary>Reads game schemas from Steam's appcache\stats\UserGameStatsSchema_&lt;appid&gt;.bin.</summary>
public sealed class SchemaService(SteamSession steam)
{
    private readonly ConcurrentDictionary<uint, CachedSchema> _cache = new();

    /// <summary>
    /// Parses the schema for <paramref name="appId"/>, with names/descriptions localized to
    /// Steam's language (falling back to English, then the raw value). Returns null if the
    /// schema file is missing or unreadable. Unknown stat types are skipped. Thread-safe;
    /// results are memoized per App ID.
    /// </summary>
    /// <remarks>
    /// The memoized result is reused until Steam rewrites the file (checked by its timestamp and
    /// size), so a schema Steam downloads later in the session is picked up. Missing or
    /// unreadable files aren't memoized.
    /// </remarks>
    public GameSchema? Load(uint appId)
    {
        try
        {
            var path = GetSchemaPath(steam.SteamInstallPath, appId);
            var file = new FileInfo(path);
            if (file.Exists == false)
            {
                this._cache.TryRemove(appId, out _);
                return null;
            }

            var stamp = (file.LastWriteTimeUtc, file.Length);
            if (this._cache.TryGetValue(appId, out var cached) && cached.Stamp == stamp)
            {
                return cached.Schema;
            }

            var schema = Parse(path, appId, steam.Language);
            if (schema != null)
            {
                this._cache[appId] = new(stamp, schema);
            }

            return schema;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't load the schema for {appId}", e);
            return null;
        }
    }

    internal static string GetSchemaPath(string steamInstallPath, uint appId) =>
        Path.Combine(steamInstallPath, "appcache", "stats", $"UserGameStatsSchema_{appId}.bin");

    /// <summary>Parses a schema file. Null if it's missing, malformed or has no "stats" section.</summary>
    internal static GameSchema? Parse(string path, uint appId, string language)
    {
        var kv = KeyValue.LoadAsBinary(path);
        if (kv == null)
        {
            return null;
        }

        var stats = kv[appId.ToString(CultureInfo.InvariantCulture)]["stats"];
        if (stats.Valid == false || stats.Children == null)
        {
            return null;
        }

        var achievements = new List<AchievementDefinition>();
        var statDefinitions = new List<StatDefinition>();

        foreach (var stat in stats.Children)
        {
            if (stat.Valid == false)
            {
                continue;
            }

            switch (GetStatType(stat))
            {
                case SchemaStatType.Integer:
                {
                    var id = stat["name"].AsString("");
                    if (id.Length == 0)
                    {
                        break;
                    }

                    statDefinitions.Add(new StatDefinition
                    {
                        Id = id,
                        DisplayName = GetLocalizedString(stat["display"]["name"], language, id),
                        Kind = StatKind.Integer,
                        MinValue = stat["min"].AsInteger(int.MinValue),
                        MaxValue = stat["max"].AsInteger(int.MaxValue),
                        MaxChange = stat["maxchange"].AsInteger(0),
                        IncrementOnly = stat["incrementonly"].AsBoolean(false),
                        SetByTrustedGameServer = stat["bSetByTrustedGS"].AsBoolean(false),
                        DefaultValue = stat["default"].AsInteger(0),
                        Permission = stat["permission"].AsInteger(0),
                    });
                    break;
                }

                case SchemaStatType.Float:
                case SchemaStatType.AverageRate:
                {
                    var id = stat["name"].AsString("");
                    if (id.Length == 0)
                    {
                        break;
                    }

                    statDefinitions.Add(new StatDefinition
                    {
                        Id = id,
                        DisplayName = GetLocalizedString(stat["display"]["name"], language, id),
                        Kind = GetStatType(stat) == SchemaStatType.Float ? StatKind.Float : StatKind.AverageRate,
                        MinValue = stat["min"].AsFloat(float.MinValue),
                        MaxValue = stat["max"].AsFloat(float.MaxValue),
                        MaxChange = stat["maxchange"].AsFloat(0.0f),
                        IncrementOnly = stat["incrementonly"].AsBoolean(false),
                        SetByTrustedGameServer = stat["bSetByTrustedGS"].AsBoolean(false),
                        DefaultValue = stat["default"].AsFloat(0.0f),
                        Permission = stat["permission"].AsInteger(0),
                    });
                    break;
                }

                case SchemaStatType.Achievements:
                case SchemaStatType.GroupAchievements:
                {
                    if (stat.Children == null)
                    {
                        break;
                    }

                    var blockId = int.TryParse(stat.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : -1;

                    foreach (var bits in stat.Children.Where(
                        c => string.Equals(c.Name, "bits", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (bits.Valid == false || bits.Children == null)
                        {
                            continue;
                        }

                        foreach (var bit in bits.Children)
                        {
                            var achievement = ParseAchievement(bit, blockId, language);
                            if (achievement != null)
                            {
                                achievements.Add(achievement);
                            }
                        }
                    }

                    break;
                }

                default:
                    // Unknown stat type: skip it rather than failing to load the whole schema.
                    break;
            }
        }

        return new GameSchema
        {
            AppId = appId,
            Achievements = achievements,
            Stats = statDefinitions,
        };
    }

    private static AchievementDefinition? ParseAchievement(KeyValue bit, int blockId, string language)
    {
        var id = bit["name"].AsString("");
        if (id.Length == 0)
        {
            return null;
        }

        var display = bit["display"];
        var iconNormal = NullIfEmpty(display["icon"].AsString(""));
        var iconLocked = NullIfEmpty(display["icon_gray"].AsString("")) ?? iconNormal;

        // Optional: progress { min_val, max_val, value { operation = "statvalue", operand1 = <stat id> } }.
        string? progressStatId = null;
        double? progressMin = null, progressMax = null;
        var progress = bit["progress"];
        if (progress.Valid && string.Equals(progress["value"]["operation"].AsString(""), "statvalue", StringComparison.OrdinalIgnoreCase))
        {
            progressStatId = NullIfEmpty(progress["value"]["operand1"].AsString(""));
            if (progressStatId != null)
            {
                progressMin = progress["min_val"].AsDouble(0);
                progressMax = progress["max_val"].AsDouble(0);
            }
        }

        return new AchievementDefinition
        {
            Id = id,
            Name = GetLocalizedString(display["name"], language, id),
            Description = GetLocalizedString(display["desc"], language, ""),
            IconNormal = iconNormal,
            IconLocked = iconLocked,
            IsHidden = display["hidden"].AsBoolean(false),
            Permission = bit["permission"].AsInteger(0),
            StatBlockId = blockId,
            BitIndex = int.TryParse(bit.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : -1,
            ProgressStatId = progressStatId,
            ProgressMin = progressMin,
            ProgressMax = progressMax,
        };
    }

    /// <summary>Equivalent of SAM.API.Types.UserStatType (not referenced so parsing stays independent of SAM.API).</summary>
    private enum SchemaStatType
    {
        Invalid = 0,
        Integer = 1,
        Float = 2,
        AverageRate = 3,
        Achievements = 4,
        GroupAchievements = 5,
    }

    private static SchemaStatType GetStatType(KeyValue stat)
    {
        // New format: type = "INT", "FLOAT", "AVGRATE", "ACHIEVEMENTS", ...
        var typeNode = stat["type"];
        if (typeNode.Valid && typeNode.Type == KeyValueType.String)
        {
            var parsed = ((string)typeNode.Value!).ToUpperInvariant() switch
            {
                "INT" or "INTEGER" => SchemaStatType.Integer,
                "FLOAT" => SchemaStatType.Float,
                "AVGRATE" or "AVERAGERATE" => SchemaStatType.AverageRate,
                "ACHIEVEMENTS" => SchemaStatType.Achievements,
                "GROUPACHIEVEMENTS" => SchemaStatType.GroupAchievements,
                _ => SchemaStatType.Invalid,
            };

            if (parsed != SchemaStatType.Invalid)
            {
                return parsed;
            }
        }

        // Old format: numeric type_int (or a numeric type).
        var typeIntNode = stat["type_int"];
        var raw = typeIntNode.Valid ? typeIntNode.AsInteger(0) : typeNode.AsInteger(0);
        return Enum.IsDefined(typeof(SchemaStatType), raw) ? (SchemaStatType)raw : SchemaStatType.Invalid;
    }

    private static string GetLocalizedString(KeyValue kv, string language, string defaultValue)
    {
        var name = kv[language].AsString("");
        if (string.IsNullOrEmpty(name) == false)
        {
            return name;
        }

        if (language != "english")
        {
            name = kv["english"].AsString("");
            if (string.IsNullOrEmpty(name) == false)
            {
                return name;
            }
        }

        name = kv.AsString("");
        return string.IsNullOrEmpty(name) == false ? name : defaultValue;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private sealed record CachedSchema((DateTime LastWrite, long Length) Stamp, GameSchema Schema);
}
