using SAM.App.Models;
using SAM.App.ViewModels;

namespace SAM.App.Tests;

public class PlaytimeSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 18, 40, 0, TimeSpan.FromHours(4));

    [Fact]
    public void SessionRunsFromLastPlayedWhenSteamStartedItAfterTheWindow()
    {
        var info = new PlaytimeInfo(Now.AddMinutes(-100), TimeSpan.FromMinutes(465));
        Assert.Equal(TimeSpan.FromMinutes(100), GameViewModel.CurrentSession(info, Now, Now.AddMinutes(-101)));
    }

    [Fact]
    public void SessionRunsFromWindowStartWhenLastPlayedIsOlder()
    {
        var info = new PlaytimeInfo(Now.AddDays(-3), TimeSpan.FromMinutes(465));
        Assert.Equal(TimeSpan.FromMinutes(20), GameViewModel.CurrentSession(info, Now, Now.AddMinutes(-20)));
    }

    [Fact]
    public void NoSessionWithoutLastPlayedOrInTheFuture()
    {
        Assert.Equal(TimeSpan.Zero, GameViewModel.CurrentSession(new PlaytimeInfo(null, TimeSpan.Zero), Now, Now.AddMinutes(-5)));
        Assert.Equal(TimeSpan.Zero, GameViewModel.CurrentSession(new PlaytimeInfo(Now.AddMinutes(5), TimeSpan.Zero), Now, Now.AddMinutes(-5)));
    }
}
