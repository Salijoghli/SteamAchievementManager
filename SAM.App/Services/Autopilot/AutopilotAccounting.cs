using SAM.App.Models;

namespace SAM.App.Services.Autopilot;

/// <summary>
/// How unlock requests are counted in a game's <see cref="AutopilotGameState"/> (UnlocksToday,
/// RecentUnlocks, LastUnlockAt, UnlockedByAutopilot). Pure, so it's unit-testable.
/// </summary>
/// <remarks>
/// A request is counted against the limits before it's sent (<see cref="Begin"/>) and its IDs are
/// kept in PendingUnlocks, so a request whose outcome is never learned (timeout, broken pipe, SAM
/// closed mid-store) still counts. A definite answer then gives back what didn't happen
/// (<see cref="Complete"/>, <see cref="Fail"/>); an uncertain one leaves everything counted
/// (<see cref="Uncertain"/>), and the next state read records what turned out unlocked
/// (<see cref="Reconcile"/>).
/// </remarks>
internal static class AutopilotAccounting
{
    /// <summary>An unlock request counted by <see cref="Begin"/>.</summary>
    internal sealed record Ticket(IReadOnlyList<string> Ids, DateTimeOffset At, DateOnly Day, DateTimeOffset? PreviousLastUnlockAt);

    /// <summary>Counts <paramref name="ids"/> as unlocked at <paramref name="now"/> and marks them pending.</summary>
    public static Ticket Begin(AutopilotGameState state, IReadOnlyList<string> ids, DateTimeOffset now)
    {
        var ticket = new Ticket(ids.Distinct(StringComparer.Ordinal).ToList(), now, state.Day, state.LastUnlockAt);
        if (ticket.Ids.Count == 0)
        {
            return ticket;
        }

        state.UnlocksToday += ticket.Ids.Count;
        for (var i = 0; i < ticket.Ids.Count; i++)
        {
            state.RecentUnlocks.Add(now);
        }

        state.LastUnlockAt = now;
        foreach (var id in ticket.Ids)
        {
            if (state.PendingUnlocks.Contains(id) == false)
            {
                state.PendingUnlocks.Add(id);
            }
        }

        return ticket;
    }

    /// <summary>The request succeeded: records <paramref name="unlocked"/> and gives back the count of requested IDs that weren't (already unlocked).</summary>
    public static void Complete(AutopilotGameState state, Ticket ticket, IReadOnlyCollection<string> unlocked)
    {
        foreach (var id in unlocked)
        {
            if (state.UnlockedByAutopilot.Contains(id) == false)
            {
                state.UnlockedByAutopilot.Add(id);
            }
        }

        var done = ticket.Ids.Count(unlocked.Contains);
        Uncount(state, ticket, ticket.Ids.Count - done, restoreLast: done == 0);
        state.PendingUnlocks.RemoveAll(ticket.Ids.Contains);
    }

    /// <summary>The request definitely failed (nothing was stored): gives back everything it counted.</summary>
    public static void Fail(AutopilotGameState state, Ticket ticket)
    {
        Uncount(state, ticket, ticket.Ids.Count, restoreLast: true);
        state.PendingUnlocks.RemoveAll(ticket.Ids.Contains);
    }

    /// <summary>The outcome is unknown: everything stays counted and pending until <see cref="Reconcile"/>.</summary>
    public static void Uncertain(AutopilotGameState state, Ticket ticket)
    {
        foreach (var id in ticket.Ids)
        {
            if (state.PendingUnlocks.Contains(id) == false)
            {
                state.PendingUnlocks.Add(id);
            }
        }
    }

    /// <summary>Remembers achievements that a raised stat might unlock as a side effect (checked by <see cref="Reconcile"/>).</summary>
    public static void Watch(AutopilotGameState state, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (state.WatchedUnlocks.Contains(id) == false)
            {
                state.WatchedUnlocks.Add(id);
            }
        }
    }

    /// <summary>
    /// After a state read (<paramref name="unlockedNow"/> = the IDs Steam reports unlocked): pending
    /// IDs that are unlocked are recorded as Autopilot's (they were counted already); watched IDs
    /// that are unlocked are recorded and counted at <paramref name="now"/>. Both lists are then
    /// cleared. Returns whether the state changed.
    /// </summary>
    public static bool Reconcile(AutopilotGameState state, IReadOnlySet<string> unlockedNow, DateTimeOffset now)
    {
        if (state.PendingUnlocks.Count == 0 && state.WatchedUnlocks.Count == 0)
        {
            return false;
        }

        foreach (var id in state.PendingUnlocks)
        {
            if (unlockedNow.Contains(id) && state.UnlockedByAutopilot.Contains(id) == false)
            {
                state.UnlockedByAutopilot.Add(id);
            }
        }

        foreach (var id in state.WatchedUnlocks)
        {
            if (unlockedNow.Contains(id) && state.UnlockedByAutopilot.Contains(id) == false && state.PendingUnlocks.Contains(id) == false)
            {
                state.UnlockedByAutopilot.Add(id);
                state.UnlocksToday++;
                state.RecentUnlocks.Add(now);
                state.LastUnlockAt = now;
            }
        }

        state.PendingUnlocks.Clear();
        state.WatchedUnlocks.Clear();
        return true;
    }

    private static void Uncount(AutopilotGameState state, Ticket ticket, int count, bool restoreLast)
    {
        if (count <= 0)
        {
            return;
        }

        // After midnight the day's counter was reset already: nothing of this request is in it.
        if (state.Day == ticket.Day)
        {
            state.UnlocksToday = Math.Max(0, state.UnlocksToday - count);
        }

        for (var i = 0; i < count; i++)
        {
            state.RecentUnlocks.Remove(ticket.At);
        }

        if (restoreLast && state.LastUnlockAt == ticket.At)
        {
            state.LastUnlockAt = ticket.PreviousLastUnlockAt;
        }
    }
}
