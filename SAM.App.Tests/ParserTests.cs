using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.Tests;

public sealed class KeyValueTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    [Fact]
    public void ParsesAllValueTypesAndNesting()
    {
        var path = this._dir.Write("kv.bin", Kv.Serialize(
            Kv.Node(
                "root",
                new Kv("text", "héllo"),
                new Kv("int", -7),
                new Kv("float", 2.5f),
                new Kv("big", 0x1_0000_0005UL),
                Kv.Node("child", new Kv("x", "1")))));

        var kv = KeyValue.LoadAsBinary(path);

        Assert.NotNull(kv);
        var root = kv!["ROOT"];
        Assert.Equal("héllo", root["text"].AsString(""));
        Assert.Equal(-7, root["int"].AsInteger(0));
        Assert.Equal(2.5f, root["float"].AsFloat(0));
        Assert.Equal(5, root["big"].AsInteger(0));
        Assert.Equal((double)0x1_0000_0005UL, root["big"].AsDouble(0));
        Assert.Equal(1, root["child"]["x"].AsInteger(0));
        Assert.True(root["child"]["x"].AsBoolean(false));
        Assert.False(root["missing"].Valid);
        Assert.Equal("dflt", root["missing"]["deeper"].AsString("dflt"));
    }

    [Fact]
    public void MissingOrTruncatedFilesReturnNull()
    {
        Assert.Null(KeyValue.LoadAsBinary(Path.Combine(this._dir.Path, "nope.bin")));

        var bytes = Kv.Serialize(Kv.Node("root", new Kv("a", 1)));
        Assert.Null(KeyValue.LoadAsBinary(this._dir.Write("truncated.bin", bytes[..^3])));
        Assert.Null(KeyValue.LoadAsBinary(this._dir.Write("trailing.bin", [.. bytes, 1, 2, 3])));
        Assert.Null(KeyValue.LoadAsBinary(this._dir.Write("badtype.bin", [42, (byte)'a', 0, 8])));
    }
}

public sealed class TextVdfTests
{
    [Fact]
    public void ParsesQuotesEscapesCommentsAndBareTokens()
    {
        const string text = """
            // leading comment
            "Root"
            {
                "quoted"    "a \"b\" c\\d\ttab"
                bare        value   // trailing comment
                "url"       "http://example.com" // slashes inside quotes aren't comments
                "cond"      "x" [$WIN32]
                "Nested" { "k" "v" }
            }
            """;

        var root = TextVdf.Parse(text);
        var node = Assert.Single(root.Nodes("root"));

        Assert.Equal("a \"b\" c\\d\ttab", node.Value("quoted"));
        Assert.Equal("value", node.Value("BARE"));
        Assert.Equal("http://example.com", node.Value("url"));
        Assert.Equal("x", node.Value("cond"));
        Assert.Equal("v", Assert.Single(node.Path("nested")).Value("K"));
    }

    [Fact]
    public void RepeatedKeysDifferingInCaseAreAllReturned()
    {
        var root = TextVdf.Parse("""
            "a" { "apps" { "1" "x" } "Apps" { "2" "y" } }
            """);

        var apps = root.Path("A", "apps").ToList();
        Assert.Equal(2, apps.Count);
        Assert.Null(root.Value("missing"));
    }

    [Fact]
    public void ToleratesUnbalancedInput()
    {
        var root = TextVdf.Parse("\"a\" { \"b\" { \"c\" \"d\"");
        Assert.Equal("d", Assert.Single(root.Path("a", "b")).Value("c"));

        Assert.Empty(TextVdf.Parse("}}}").Entries);
        Assert.Empty(TextVdf.Parse("").Entries);
    }
}

public sealed class SchemaParsingTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    private GameSchema Parse(string language)
    {
        var path = this._dir.Write("schema.bin", Fixtures.Schema());
        return SchemaService.Parse(path, Fixtures.AppId, language)!;
    }

    [Fact]
    public void ReadsAchievementsFromNewAndOldTypeFormats()
    {
        var schema = this.Parse("english");

        Assert.Equal(["ACH_A", "ACH_B", "ACH_C", "ACH_D"], schema.Achievements.Select(a => a.Id));
        var d = schema.Achievements[3];
        Assert.Equal(2, d.StatBlockId);
        Assert.Equal(0, d.BitIndex);
        Assert.Equal("Delta", d.Name);
        Assert.Equal("Do Delta", d.Description);
        Assert.Equal("d.jpg", d.IconNormal);
        Assert.Equal("d.jpg", d.IconLocked);

        var c = schema.Achievements[2];
        Assert.Equal(1, c.StatBlockId);
        Assert.Equal(2, c.BitIndex);
    }

    [Fact]
    public void LocalizesNamesWithEnglishFallback()
    {
        var schema = this.Parse("german");
        Assert.Equal("Alpha-DE", schema.Achievements[0].Name);
        Assert.Equal("Beta", schema.Achievements[1].Name);
        Assert.Equal("Do Beta", schema.Achievements[1].Description);
    }

    [Fact]
    public void ReadsProgressBlocks()
    {
        var schema = this.Parse("english");
        var b = schema.Achievements[1];
        Assert.Equal("STAT_KILLS", b.ProgressStatId);
        Assert.Equal(0, b.ProgressMin);
        Assert.Equal(50, b.ProgressMax);
        Assert.Null(schema.Achievements[0].ProgressStatId);
        Assert.Null(schema.Achievements[0].ProgressMax);
    }

    [Fact]
    public void ReadsStatsAndSkipsUnknownTypes()
    {
        var schema = this.Parse("english");

        Assert.Equal(["STAT_KILLS", "STAT_DIST"], schema.Stats.Select(s => s.Id));
        var kills = schema.Stats[0];
        Assert.Equal(StatKind.Integer, kills.Kind);
        Assert.Equal("Kills", kills.DisplayName);
        Assert.Equal(100, kills.MaxValue);
        Assert.Equal(5, kills.DefaultValue);
        Assert.True(kills.IncrementOnly);

        var distance = schema.Stats[1];
        Assert.Equal(StatKind.Float, distance.Kind);
        Assert.Equal("Distance", distance.DisplayName);
        Assert.Equal(1.5, distance.DefaultValue);
    }

    [Fact]
    public void ReturnsNullForWrongAppOrMissingFile()
    {
        var path = this._dir.Write("schema.bin", Fixtures.Schema());
        Assert.Null(SchemaService.Parse(path, Fixtures.AppId + 1, "english"));
        Assert.Null(SchemaService.Parse(Path.Combine(this._dir.Path, "missing.bin"), Fixtures.AppId, "english"));
    }
}

public sealed class ProgressTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    [Fact]
    public void CountsOnlySetBits()
    {
        var schema = SchemaService.Parse(this._dir.Write("schema.bin", Fixtures.Schema()), Fixtures.AppId, "english")!;
        var stats = this._dir.Write("stats.bin", Fixtures.Stats());

        // ACH_A, ACH_C and ACH_D are set; ACH_B was re-locked but still has a time.
        Assert.Equal(new GameProgress(3, 4), ProgressService.Compute(stats, schema));
    }

    [Fact]
    public void MissingCacheMeansNothingUnlockedAndCorruptMeansUnknown()
    {
        var schema = SchemaService.Parse(this._dir.Write("schema.bin", Fixtures.Schema()), Fixtures.AppId, "english")!;

        Assert.Equal(new GameProgress(0, 4), ProgressService.Compute(Path.Combine(this._dir.Path, "none.bin"), schema));
        Assert.Null(ProgressService.Compute(this._dir.Write("bad.bin", [1, 2, 3]), schema));
    }

    [Fact]
    public void BuildsStatsPath()
    {
        Assert.Equal(
            Path.Combine("C:\\Steam", "appcache", "stats", "UserGameStats_7_42.bin"),
            ProgressService.GetStatsPath("C:\\Steam", 7, 42));
    }
}

public sealed class PlaytimeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    [Fact]
    public void ReadsLastPlayedAndPlaytime()
    {
        var path = this._dir.Write("localconfig.vdf", """
            "UserLocalConfigStore"
            {
                "Software"
                {
                    "Valve"
                    {
                        "Steam"
                        {
                            "apps"
                            {
                                "10" { "LastPlayed" "1700000000" "Playtime" "90" }
                                "20" { "Playtime" "5" }
                                "30" { "cloud" { "last_sync_state" "synchronized" } }
                                "notanid" { "Playtime" "1" }
                            }
                            "Apps"
                            {
                                "10" { "LastPlayed" "1600000000" "Playtime" "120" }
                            }
                        }
                    }
                }
            }
            """);

        var result = PlaytimeService.Parse(path);

        Assert.Equal(2, result.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), result[10].LastPlayed);
        Assert.Equal(TimeSpan.FromMinutes(120), result[10].Playtime);
        Assert.Null(result[20].LastPlayed);
        Assert.Equal(TimeSpan.FromMinutes(5), result[20].Playtime);
    }

    [Fact]
    public void MissingFileIsEmpty() =>
        Assert.Empty(PlaytimeService.Parse(Path.Combine(this._dir.Path, "missing.vdf")));
}

public sealed class GlobalPercentTests
{
    [Fact]
    public void ParsesNumberAndStringPercents()
    {
        var result = GlobalPercentService.ParseResponse("""
            {"achievementpercentages":{"achievements":[
                {"name":"A","percent":12.5},
                {"name":"B","percent":"3.25"},
                {"name":"C","percent":"oops"},
                {"name":"D","percent":150},
                {"percent":1},
                {"name":"","percent":1}
            ]}}
            """);

        Assert.Equal(3, result.Count);
        Assert.Equal(12.5, result["A"]);
        Assert.Equal(3.25, result["B"]);
        Assert.Equal(100, result["D"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"achievementpercentages\":{}}")]
    [InlineData("{\"achievementpercentages\":{\"achievements\":{}}}")]
    public void MissingListIsEmpty(string json) => Assert.Empty(GlobalPercentService.ParseResponse(json));
}
