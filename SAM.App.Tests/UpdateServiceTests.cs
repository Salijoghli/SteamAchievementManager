using System.Net;
using System.Text;
using SAM.App.Services;

namespace SAM.App.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    private static string Release(string tag, params (string Name, string Url, long Size)[] assets)
    {
        var list = string.Join(",", assets.Select(a => $$"""{"name":"{{a.Name}}","browser_download_url":"{{a.Url}}","size":{{a.Size}}}"""));
        return $$"""
            {
              "tag_name": "{{tag}}",
              "name": "Steam Achievement Manager {{tag}}",
              "html_url": "https://github.com/Salijoghli/SteamAchievementManager/releases/tag/{{tag}}",
              "body": "Notes",
              "assets": [{{list}}]
            }
            """;
    }

    private static readonly (string, string, long) Zip = ("SAM-v99.0.0.zip", "https://github.com/x/SAM-v99.0.0.zip", 30_000_000);
    private static readonly (string, string, long) Exe = ("SAM.exe", "https://github.com/x/SAM.exe", 70_000_000);

    private UpdateService Service(string json, HttpStatusCode status = HttpStatusCode.OK, string? skipped = null)
    {
        var settings = AppSettings.Load(Path.Combine(this._dir.Path, "settings.json"));
        settings.SkippedUpdateVersion = skipped;
        return new UpdateService(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(json) })), settings);
    }

    [Fact]
    public void PicksTheExeAssetForSelfUpdateAndTheZipForDownload()
    {
        var json = Release("v99.0.0", Zip, ("SAM.exe.sha256", "https://github.com/x/SAM.exe.sha256", 64), Exe);

        var exe = UpdateService.ParseExecutableAsset(json);
        var info = UpdateService.ParseRelease(json);

        Assert.Equal(new UpdateAsset("SAM.exe", new Uri("https://github.com/x/SAM.exe"), 70_000_000), exe);
        Assert.Equal(new Uri("https://github.com/x/SAM-v99.0.0.zip"), info!.DownloadUrl);
        Assert.Equal("99.0.0", info.Version);
    }

    [Theory]
    [InlineData("sam.EXE", "https://github.com/x/sam.EXE", 1, true)]
    [InlineData("SAM.exe", "http://github.com/x/SAM.exe", 70_000_000, false)]
    [InlineData("SAM.exe", "not a url", 70_000_000, false)]
    [InlineData("SAM.exe", "https://github.com/x/SAM.exe", 0, false)]
    [InlineData("SAM-setup.exe", "https://github.com/x/SAM-setup.exe", 70_000_000, false)]
    public void ExeAssetRules(string name, string url, long size, bool accepted)
    {
        var asset = UpdateService.ParseExecutableAsset(Release("v99.0.0", (name, url, size)));

        Assert.Equal(accepted, asset != null);
    }

    [Fact]
    public void NoExeAssetWithoutAssets()
    {
        Assert.Null(UpdateService.ParseExecutableAsset("""{"tag_name":"v1"}"""));
        Assert.Null(UpdateService.ParseExecutableAsset(Release("v99.0.0", Zip)));
    }

    [Theory]
    [InlineData("8.1.5.0", "8.1.5", true)]
    [InlineData("8.1.5.0", "v8.1.5", true)]
    [InlineData("8.1.5", "v8.1.5-beta", true)]
    [InlineData("8.1.4.0", "8.1.5", false)]
    [InlineData("8.1.5.1", "8.1.5", false)]
    [InlineData(null, "8.1.5", false)]
    [InlineData("", "8.1.5", false)]
    [InlineData("8.1.5.0", "latest", false)]
    public void ComparesFileVersionWithReleaseVersion(string? fileVersion, string releaseVersion, bool same)
    {
        Assert.Equal(same, UpdateService.IsSameVersion(fileVersion, releaseVersion));
    }

    [Fact]
    public async Task NewerReleaseOffersItsExe()
    {
        var service = this.Service(Release("v99.0.0", Zip, Exe));

        var info = await service.CheckAsync(CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("99.0.0", info!.Version);
        Assert.Equal("SAM.exe", (await service.GetExecutableAsync(info, CancellationToken.None))?.Name);
    }

    [Fact]
    public async Task OlderOrSkippedReleasesAreNotOffered()
    {
        Assert.Null(await this.Service(Release("v1.0.0", Exe)).CheckAsync(CancellationToken.None));
        Assert.Null(await this.Service(Release("v99.0.0", Exe), skipped: "99.0.0").CheckAsync(CancellationToken.None));
        Assert.Null(await this.Service("", HttpStatusCode.NotFound).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NoExeForAReleaseThatIsNotTheLatest()
    {
        var service = this.Service(Release("v99.0.0", Exe));
        var stale = new Models.UpdateInfo("98.0.0", "Old", "", new Uri("https://github.com/"), null);

        Assert.Null(await service.GetExecutableAsync(stale, CancellationToken.None));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}

public sealed class SelfUpdaterTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    /// <summary>SAM.dll: a real PE file whose FileVersion is the app's version.</summary>
    private static string AppAssembly => typeof(UpdateService).Assembly.Location;

    private static string AppVersion => UpdateService.CurrentVersion.ToString(3);

    [Fact]
    public void AcceptsAProgramOfTheReleaseVersion()
    {
        var size = new FileInfo(AppAssembly).Length;

        Assert.Equal(UpdateVerification.Ok, SelfUpdater.Verify(AppAssembly, AppVersion, size, minimumSize: 0));
        Assert.Equal(UpdateVerification.Ok, SelfUpdater.Verify(AppAssembly, "v" + AppVersion, null, minimumSize: 0));
    }

    [Fact]
    public void RejectsAnotherVersion()
    {
        Assert.Equal(UpdateVerification.WrongVersion, SelfUpdater.Verify(AppAssembly, "99.0.0", null, minimumSize: 0));
    }

    [Fact]
    public void RejectsSmallTruncatedOrMissingFiles()
    {
        var size = new FileInfo(AppAssembly).Length;

        // The real minimum: a self-contained SAM.exe is tens of MB.
        Assert.Equal(UpdateVerification.Incomplete, SelfUpdater.Verify(AppAssembly, AppVersion, size));
        Assert.Equal(UpdateVerification.Incomplete, SelfUpdater.Verify(AppAssembly, AppVersion, size + 1, minimumSize: 0));
        Assert.Equal(UpdateVerification.Incomplete, SelfUpdater.Verify(Path.Combine(this._dir.Path, "missing.exe"), AppVersion, null, minimumSize: 0));
    }

    [Fact]
    public void RejectsFilesThatAreNotPrograms()
    {
        var html = this._dir.Write("page.exe", "<html>" + new string(' ', 4096) + "</html>");
        var mzOnly = new byte[4096];
        mzOnly[0] = (byte)'M';
        mzOnly[1] = (byte)'Z';
        mzOnly[0x3C] = 0x80;
        var fake = this._dir.Write("fake.exe", mzOnly);

        Assert.Equal(UpdateVerification.NotExecutable, SelfUpdater.Verify(html, AppVersion, null, minimumSize: 0));
        Assert.Equal(UpdateVerification.NotExecutable, SelfUpdater.Verify(fake, AppVersion, null, minimumSize: 0));
        Assert.True(SelfUpdater.IsPortableExecutable(AppAssembly));
    }

    [Fact]
    public void SwapsTheNewExeIntoPlaceAndKeepsTheOldOne()
    {
        var exe = this._dir.Write("SAM.exe", "old");
        var download = this._dir.Write("SAM.exe.download", "new");

        SelfUpdater.Swap(exe, download);

        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(download));
    }

    [Fact]
    public void UsesAnotherNameWhenTheOldExeIsStillInUse()
    {
        var exe = this._dir.Write("SAM.exe", "current");
        var download = this._dir.Write("SAM.exe.download", "new");
        this._dir.Write("SAM.exe.old", "older");

        using (new FileStream(exe + ".old", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            SelfUpdater.Swap(exe, download);
        }

        Assert.Equal("new", File.ReadAllText(exe));
        var kept = Directory.GetFiles(this._dir.Path, "SAM.exe.*.old");
        Assert.Equal("current", File.ReadAllText(Assert.Single(kept)));
    }

    [Fact]
    public void PutsTheOldExeBackWhenTheSwapFails()
    {
        var exe = this._dir.Write("SAM.exe", "current");

        Assert.ThrowsAny<IOException>(() => SelfUpdater.Swap(exe, Path.Combine(this._dir.Path, "missing.download")));

        Assert.Equal("current", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void CleanUpRemovesLeftoversOnly()
    {
        var exe = this._dir.Write("SAM.exe", "current");
        this._dir.Write("SAM.exe.old", "old");
        this._dir.Write("SAM.exe.638000000000000000.old", "older");
        this._dir.Write("SAM.exe.download", "partial");
        this._dir.Write("notes.old", "not ours");

        SelfUpdater.CleanUp(exe);

        Assert.Equal(["notes.old", "SAM.exe"], Directory.GetFiles(this._dir.Path).Select(p => Path.GetFileName(p)).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanWriteToATempFolder()
    {
        Assert.True(SelfUpdater.CanWriteTo(this._dir.Path));
        Assert.False(SelfUpdater.CanWriteTo(Path.Combine(this._dir.Path, "missing")));
    }

    [Fact]
    public async Task DownloadsWithProgress()
    {
        var content = Encoding.ASCII.GetBytes(new string('x', 300_000));
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));
        var target = Path.Combine(this._dir.Path, "SAM.exe.download");
        var reports = new List<double>();

        await SelfUpdater.DownloadAsync(http, new UpdateAsset("SAM.exe", new Uri("https://example.invalid/SAM.exe"), content.Length), target, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(content, File.ReadAllBytes(target));
        Assert.Equal(1.0, reports[^1]);
    }

    [Fact]
    public async Task FailedDownloadLeavesNoFile()
    {
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var target = Path.Combine(this._dir.Path, "SAM.exe.download");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            SelfUpdater.DownloadAsync(http, new UpdateAsset("SAM.exe", new Uri("https://example.invalid/SAM.exe"), 10), target, null, CancellationToken.None));

        Assert.False(File.Exists(target));
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
