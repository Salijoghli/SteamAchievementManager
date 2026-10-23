using System.Runtime.InteropServices;

namespace SAM.App.Services.Autopilot;

/// <summary>
/// Time the PC has been awake, for crediting Autopilot running time: it doesn't advance while
/// Windows sleeps or hibernates (QueryUnbiasedInterruptTime), so a suspended PC isn't credited
/// with playtime however late the next timer tick comes.
/// </summary>
internal static class AwakeClock
{
    /// <summary>Most time credited per step when the awake clock isn't available (the old behaviour).</summary>
    private static readonly TimeSpan FallbackMaxCredit = TimeSpan.FromMinutes(2);

    /// <summary>A reading of both clocks.</summary>
    internal readonly record struct Mark(DateTimeOffset Wall, TimeSpan Awake);

    /// <summary>Awake time since boot, or null if Windows can't tell.</summary>
    public static TimeSpan? Awake
    {
        get
        {
            try
            {
                return QueryUnbiasedInterruptTime(out var time) ? TimeSpan.FromTicks((long)time) : null;
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
                return null;
            }
        }
    }

    public static Mark Now(DateTimeOffset wall) => new(wall, Awake ?? TimeSpan.MinValue);

    /// <summary>
    /// Running time to credit between two marks: the smaller of the wall-clock and awake time that
    /// passed (never negative). Without awake readings, the wall-clock time capped at two minutes.
    /// </summary>
    public static TimeSpan Elapsed(Mark from, Mark to)
    {
        var wall = to.Wall - from.Wall;
        if (wall <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (from.Awake == TimeSpan.MinValue || to.Awake == TimeSpan.MinValue)
        {
            return wall > FallbackMaxCredit ? FallbackMaxCredit : wall;
        }

        var awake = to.Awake - from.Awake;
        if (awake <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return awake < wall ? awake : wall;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
