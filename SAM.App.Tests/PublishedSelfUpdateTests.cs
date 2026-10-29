using System.Diagnostics;
using SAM.App.Services;

namespace SAM.App.Tests;

/// <summary>
/// Self-update against real published builds: set SAM_UPDATE_TEST_DIR to a folder with
/// old\SAM.exe (the current version) and new\SAM.exe (published with -p:Version=99.0.0). Works on
/// copies in a temp folder only.
/// </summary>
public sealed class PublishedSelfUpdateTests : IDisposable
{
    private static readonly string? Builds = Environment.GetEnvironmentVariable("SAM_UPDATE_TEST_DIR");

    private readonly TempDir _dir = new();

    public void Dispose() => this._dir.Dispose();

    [PublishedBuildsFact]
    public void SwapsARunningExeForTheVerifiedNewOne()
    {
        var exe = Path.Combine(this._dir.Path, "SAM.exe");
        var download = exe + SelfUpdater.DownloadSuffix;
        File.Copy(Path.Combine(Builds!, "old", "SAM.exe"), exe);
        File.Copy(Path.Combine(Builds!, "new", "SAM.exe"), download);

        Assert.Equal(UpdateVerification.Ok, SelfUpdater.Verify(download, "v99.0.0", new FileInfo(download).Length));
        Assert.Equal(UpdateVerification.WrongVersion, SelfUpdater.Verify(exe, "v99.0.0", null));

        // "--help" without a console shows a window and keeps running (no Steam involved).
        using var running = StartWithoutConsole(exe, "--help");
        try
        {
            Thread.Sleep(3000);
            Assert.False(running.HasExited);

            SelfUpdater.Swap(exe, download);

            Assert.Equal("99.0.0.0", FileVersionInfo.GetVersionInfo(exe).FileVersion);
            Assert.Equal(UpdateService.CurrentVersion.ToString(), FileVersionInfo.GetVersionInfo(exe + SelfUpdater.OldSuffix).FileVersion);

            // The old exe is still in use: clean-up leaves it for next time.
            SelfUpdater.CleanUp(exe);
            Assert.True(File.Exists(exe + SelfUpdater.OldSuffix));
        }
        finally
        {
            running.Kill();
            running.WaitForExit(10_000);
        }

        SelfUpdater.CleanUp(exe);
        Assert.False(File.Exists(exe + SelfUpdater.OldSuffix));
        Assert.True(File.Exists(exe));
    }

    /// <summary>
    /// Starts <paramref name="exe"/> through WMI so its parent has no console to attach to (the
    /// test host has one, and SAM would print "--help" there and exit).
    /// </summary>
    private static Process StartWithoutConsole(string exe, string arguments)
    {
        var command = $"(Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{{CommandLine='\"{exe}\" {arguments}'}}).ProcessId";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using var shell = Process.Start(start)!;
        var output = shell.StandardOutput.ReadToEnd();
        shell.WaitForExit();
        return Process.GetProcessById(int.Parse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class PublishedBuildsFactAttribute : FactAttribute
    {
        public PublishedBuildsFactAttribute()
        {
            if (string.IsNullOrEmpty(Builds))
            {
                this.Skip = "Set SAM_UPDATE_TEST_DIR to a folder with old\\SAM.exe and new\\SAM.exe to run.";
            }
        }
    }
}
