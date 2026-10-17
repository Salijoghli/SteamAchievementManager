namespace SAM.App.Tests;

/// <summary>Synthetic schema and stats-cache files shaped like Steam's appcache\stats\*.bin.</summary>
internal static class Fixtures
{
    public const uint AppId = 4242;

    public static Kv Achievement(string bit, string id, string english, string? german = null, string icon = "", Kv? progress = null)
    {
        var names = new List<Kv> { new("english", english) };
        if (german != null)
        {
            names.Add(new("german", german));
        }

        var children = new List<Kv>
        {
            new("name", id),
            Kv.Node(
                "display",
                Kv.Node("name", [.. names]),
                Kv.Node("desc", new Kv("english", $"Do {english}")),
                new("icon", icon),
                new("icon_gray", ""),
                new("hidden", "0")),
            new("permission", 0),
        };
        if (progress != null)
        {
            children.Add(progress);
        }

        return Kv.Node(bit, [.. children]);
    }

    /// <summary>
    /// Block 1 (new string type): ACH_A bit 0, ACH_B bit 1 (with progress), ACH_C bit 2.
    /// Block 2 (old numeric type_int 4): ACH_D bit 0. Plus an INT stat, an old-style float stat
    /// (type_int 2) and an unknown type that must be skipped.
    /// </summary>
    public static byte[] Schema() => Kv.Serialize(
        Kv.Node(
            AppId.ToString(),
            Kv.Node(
                "stats",
                Kv.Node(
                    "1",
                    new Kv("type", "ACHIEVEMENTS"),
                    new Kv("id", 1),
                    Kv.Node(
                        "bits",
                        Achievement("0", "ACH_A", "Alpha", german: "Alpha-DE", icon: "a.jpg"),
                        Achievement(
                            "1",
                            "ACH_B",
                            "Beta",
                            icon: "b.jpg",
                            progress: Kv.Node(
                                "progress",
                                new Kv("min_val", "0"),
                                new Kv("max_val", "50"),
                                Kv.Node("value", new Kv("operation", "statvalue"), new Kv("operand1", "STAT_KILLS")))),
                        Achievement("2", "ACH_C", "Gamma", icon: "c.jpg"))),
                Kv.Node(
                    "2",
                    new Kv("type_int", 4),
                    Kv.Node("bits", Achievement("0", "ACH_D", "Delta", icon: "d.jpg"))),
                Kv.Node(
                    "3",
                    new Kv("type", "INT"),
                    new Kv("name", "STAT_KILLS"),
                    new Kv("min", "0"),
                    new Kv("max", "100"),
                    new Kv("default", "5"),
                    new Kv("incrementonly", "1"),
                    Kv.Node("display", Kv.Node("name", new Kv("english", "Kills")))),
                Kv.Node(
                    "4",
                    new Kv("type_int", 2),
                    new Kv("name", "STAT_DIST"),
                    new Kv("default", "1.5"),
                    Kv.Node("display", new Kv("name", "Distance"))),
                Kv.Node(
                    "5",
                    new Kv("type", "SOMETHING_NEW"),
                    new Kv("name", "STAT_UNKNOWN")))));

    /// <summary>
    /// Block 1: ACH_A (bit 0) unlocked at 1000, ACH_C (bit 2) unlocked with no time, ACH_B (bit 1)
    /// re-locked but with a leftover time. Block 2: ACH_D (bit 0) unlocked at 2000.
    /// </summary>
    public static byte[] Stats() => Kv.Serialize(
        Kv.Node(
            "cache",
            new Kv("crc", 12345),
            Kv.Node(
                "1",
                new Kv("data", 0b101),
                Kv.Node("AchievementTimes", new Kv("0", 1000), new Kv("1", 1500))),
            Kv.Node(
                "2",
                new Kv("data", 0b1),
                Kv.Node("AchievementTimes", new Kv("0", 2000))),
            new Kv("PendingChanges", 0)));
}
