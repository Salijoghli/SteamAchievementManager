using System.Globalization;

namespace SAM.App.Infrastructure;

/// <summary>What a launch asks for, after parsing its command line.</summary>
public enum LaunchMode
{
    /// <summary>Open the game library (no arguments).</summary>
    Library,

    /// <summary>Open the achievement manager window for <see cref="CommandLine.AppId"/>.</summary>
    GameWindow,

    /// <summary>Change achievements without a window (<c>--commit</c>).</summary>
    Commit,

    /// <summary>Print usage (<c>--help</c>).</summary>
    Help,

    /// <summary>The arguments are invalid; see <see cref="CommandLine.Error"/>.</summary>
    Invalid,

    /// <summary>
    /// Internal: a headless Autopilot worker for <see cref="CommandLine.AppId"/>, started by the
    /// library (<c>&lt;appid&gt; --autopilot-worker &lt;parent pid&gt;</c>).
    /// </summary>
    AutopilotWorker,

    /// <summary>
    /// Ask Steam for <see cref="CommandLine.AppId"/>'s stats so it downloads the achievement schema,
    /// then exit, without a window (<c>&lt;appid&gt; --fetch-achievements</c>).
    /// </summary>
    FetchAchievements,
}

/// <summary>
/// Parses SAM's command line:
/// <c>SAM.exe [&lt;appid&gt; [--unlock-all | --lock-all]]</c> for the windows, and
/// <c>SAM.exe &lt;appid&gt; [--unlock ID[,ID…]] [--lock ID[,ID…]] [--unlock-all-cli] --commit [--quiet] [--no-backup]</c>
/// to change achievements without a window, and <c>SAM.exe --autopilot</c> to start the library in
/// the tray with Autopilot running, and <c>SAM.exe &lt;appid&gt; --fetch-achievements</c> to have Steam
/// download a game's achievement data. Pure and side-effect free, so it can be unit tested.
/// </summary>
public sealed class CommandLine
{
    /// <summary>Exit code: everything worked (or there was nothing to change).</summary>
    public const int ExitSuccess = 0;

    /// <summary>Exit code: Steam or the commit failed; nothing (or not everything) was stored.</summary>
    public const int ExitFailed = 1;

    /// <summary>Exit code: invalid arguments or achievement IDs; nothing was changed.</summary>
    public const int ExitInvalidArguments = 2;

    /// <summary>Exit code: the game's window is open in SAM; nothing was changed.</summary>
    public const int ExitBusy = 3;

    /// <summary>Internal option used to restart the library: <c>--restart-after=&lt;pid&gt;</c>.</summary>
    public const string RestartAfterOption = "--restart-after=";

    /// <summary>Starts the library hidden in the tray with Autopilot running (used by "Start with Windows").</summary>
    public const string AutopilotOption = "--autopilot";

    /// <summary>Internal option the Autopilot engine starts its workers with: <c>&lt;appid&gt; --autopilot-worker &lt;parent pid&gt;</c>.</summary>
    public const string AutopilotWorkerOption = "--autopilot-worker";

    /// <summary>Internal option the engine adds in Autopilot dry-run mode: the worker only logs what it would change.</summary>
    public const string AutopilotDryRunOption = "--autopilot-dry-run";

    /// <summary>Downloads a game's achievement data without a window: <c>&lt;appid&gt; --fetch-achievements</c>.</summary>
    public const string FetchAchievementsOption = "--fetch-achievements";

    /// <summary>Usage text printed by <c>--help</c>.</summary>
    public const string Usage =
        """
        Steam Achievement Manager

        Usage:
          SAM.exe                          Open the game library.
          SAM.exe <appid>                  Open the achievement manager for a game.
          SAM.exe <appid> --unlock-all     ...with every achievement set to unlocked (confirm in the window).
          SAM.exe <appid> --lock-all       ...with every achievement set to locked (confirm in the window).
          SAM.exe --autopilot              Start the library in the tray and start Autopilot.
          SAM.exe <appid> --fetch-achievements
                                           Ask Steam to download the game's achievement data
                                           (read-only; Steam briefly shows the game as playing).

          SAM.exe <appid> [--unlock ID[,ID...]] [--lock ID[,ID...]] [--unlock-all-cli] --commit [options]
                                           Change achievements without opening a window.

        Options:
          --unlock ID[,ID...]   Achievement API names to unlock (repeatable).
          --lock ID[,ID...]     Achievement API names to lock (repeatable).
          --unlock-all-cli      Unlock every achievement that isn't protected.
          --commit              Store the changes on Steam now, without a window. Required with the
                                options above. A backup is saved first.
          --no-backup           Don't save a backup before committing.
          --quiet               Don't show a notification with the result when there's no console.
          --help                Show this help.

        Unknown or protected achievement IDs are an error and nothing is changed. The game's SAM
        window must be closed while committing.

        Exit codes: 0 success, 1 Steam or the commit failed, 2 invalid arguments, 3 the game's
        window is open.

        SAM is a Windows app, so cmd and PowerShell don't wait for it. To wait for the result use
        "start /wait SAM.exe ..." (cmd) or "Start-Process SAM.exe ... -Wait" (PowerShell).
        """;

    private CommandLine()
    {
    }

    public LaunchMode Mode { get; private init; }

    /// <summary>The game's App ID; 0 for the library, help and some invalid launches.</summary>
    public uint AppId { get; private init; }

    /// <summary>GUI preset: open the game window with every achievement set to unlocked.</summary>
    public bool UnlockAllPreset { get; private init; }

    /// <summary>GUI preset: open the game window with every achievement set to locked.</summary>
    public bool LockAllPreset { get; private init; }

    /// <summary>Achievement IDs to unlock (<c>--unlock</c>), in order, without duplicates.</summary>
    public IReadOnlyList<string> Unlock { get; private init; } = [];

    /// <summary>Achievement IDs to lock (<c>--lock</c>), in order, without duplicates.</summary>
    public IReadOnlyList<string> Lock { get; private init; } = [];

    /// <summary><c>--unlock-all-cli</c>: unlock every achievement that isn't protected.</summary>
    public bool UnlockAll { get; private init; }

    /// <summary><c>--quiet</c>: no result notification when there's no console.</summary>
    public bool Quiet { get; private init; }

    /// <summary><c>--no-backup</c>: skip the backup before committing.</summary>
    public bool NoBackup { get; private init; }

    /// <summary>
    /// Set by the Settings dialog's "Restart now" (<c>--restart-after=&lt;pid&gt;</c>): the process to
    /// wait for before claiming the single-instance lock. 0 when absent.
    /// </summary>
    public int RestartAfterProcessId { get; private init; }

    /// <summary><c>--autopilot</c>: start the library hidden in the tray and start Autopilot.</summary>
    public bool StartAutopilot { get; private init; }

    /// <summary>Autopilot worker: the library process to watch (the worker exits when it does).</summary>
    public int ParentProcessId { get; private init; }

    /// <summary>Autopilot worker: <c>--autopilot-dry-run</c>, log unlocks instead of making them.</summary>
    public bool AutopilotDryRun { get; private init; }

    /// <summary>Why the arguments are invalid (when <see cref="Mode"/> is <see cref="LaunchMode.Invalid"/>).</summary>
    public string? Error { get; private init; }

    /// <summary>
    /// Parses <paramref name="arguments"/> (without the executable path). Never throws; invalid
    /// input gives <see cref="LaunchMode.Invalid"/> with an <see cref="Error"/>.
    /// </summary>
    public static CommandLine Parse(IReadOnlyList<string> arguments)
    {
        // Windows adds these when it starts the app for a clicked notification (or as a COM server).
        var args = arguments
            .Where(a => a.StartsWith("----", StringComparison.Ordinal) == false &&
                        string.Equals(a, "-Embedding", StringComparison.OrdinalIgnoreCase) == false)
            .ToList();

        var restartAfter = 0;
        var restart = args.FindIndex(a => a.StartsWith(RestartAfterOption, StringComparison.OrdinalIgnoreCase));
        if (restart >= 0)
        {
            int.TryParse(args[restart][RestartAfterOption.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out restartAfter);
            args.RemoveAt(restart);
            if (args.Count == 0)
            {
                return new CommandLine { Mode = LaunchMode.Library, RestartAfterProcessId = restartAfter };
            }
        }

        if (args.Count == 1 && string.Equals(args[0], AutopilotOption, StringComparison.OrdinalIgnoreCase))
        {
            return new CommandLine { Mode = LaunchMode.Library, StartAutopilot = true, RestartAfterProcessId = restartAfter };
        }

        if (args.Any(IsHelp))
        {
            return new CommandLine { Mode = LaunchMode.Help };
        }

        if (args.Count == 0)
        {
            return new CommandLine { Mode = LaunchMode.Library };
        }

        var isNumber = uint.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var appId);
        if (isNumber && appId == 0 && args.Count == 1)
        {
            // "SAM.exe 0" has always meant the library.
            return new CommandLine { Mode = LaunchMode.Library };
        }

        if (isNumber == false || appId == 0)
        {
            return Invalid(args[0].StartsWith('-')
                ? $"Put the game's App ID before {args[0]}, e.g. SAM.exe 480 {args[0]} ..."
                : $"\"{args[0]}\" is not a valid Steam App ID.");
        }

        var unlock = new List<string>();
        var @lock = new List<string>();
        bool unlockAllPreset = false, lockAllPreset = false, unlockAll = false, commit = false, quiet = false, noBackup = false;
        var workerParent = 0;
        var workerDryRun = false;
        var fetch = false;

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            var name = arg;
            string? value = null;
            var equals = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                name = arg[..equals];
                value = arg[(equals + 1)..];
            }

            switch (name.ToLowerInvariant())
            {
                case "--unlock":
                case "--lock":
                {
                    if (value == null)
                    {
                        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            return Invalid($"{name} needs one or more achievement IDs, e.g. {name} ACH_WIN,ACH_LOSE.", appId);
                        }

                        value = args[++i];
                    }

                    var ids = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (ids.Length == 0)
                    {
                        return Invalid($"{name} needs one or more achievement IDs.", appId);
                    }

                    var target = name.Equals("--unlock", StringComparison.OrdinalIgnoreCase) ? unlock : @lock;
                    foreach (var id in ids)
                    {
                        if (target.Contains(id, StringComparer.Ordinal) == false)
                        {
                            target.Add(id);
                        }
                    }

                    break;
                }

                case "--unlock-all" when value == null:
                    unlockAllPreset = true;
                    break;
                case "--lock-all" when value == null:
                    lockAllPreset = true;
                    break;
                case "--unlock-all-cli" when value == null:
                    unlockAll = true;
                    break;
                case "--commit" when value == null:
                    commit = true;
                    break;
                case "--quiet" when value == null:
                    quiet = true;
                    break;
                case "--no-backup" when value == null:
                    noBackup = true;
                    break;
                case FetchAchievementsOption when value == null:
                    fetch = true;
                    break;
                case AutopilotDryRunOption when value == null:
                    workerDryRun = true;
                    break;
                case AutopilotWorkerOption:
                {
                    if (value == null && i + 1 < args.Count && args[i + 1].StartsWith("-", StringComparison.Ordinal) == false)
                    {
                        value = args[++i];
                    }

                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out workerParent) == false || workerParent <= 0)
                    {
                        return Invalid($"{AutopilotWorkerOption} needs the parent process ID.", appId);
                    }

                    break;
                }

                default:
                    return Invalid($"Unknown option \"{arg}\".", appId);
            }
        }

        if (fetch)
        {
            if (args.Count != 2)
            {
                return Invalid($"{FetchAchievementsOption} can't be combined with other options.", appId);
            }

            return new CommandLine { Mode = LaunchMode.FetchAchievements, AppId = appId };
        }

        if (workerParent != 0)
        {
            if (unlock.Count > 0 || @lock.Count > 0 || unlockAllPreset || lockAllPreset || unlockAll || commit || quiet || noBackup)
            {
                return Invalid($"{AutopilotWorkerOption} can't be combined with other options.", appId);
            }

            return new CommandLine { Mode = LaunchMode.AutopilotWorker, AppId = appId, ParentProcessId = workerParent, AutopilotDryRun = workerDryRun };
        }

        if (workerDryRun)
        {
            return Invalid($"{AutopilotDryRunOption} only applies to {AutopilotWorkerOption}.", appId);
        }

        // Case-insensitive: the runner resolves IDs that differ only in case to the same achievement.
        var both = unlock.Intersect(@lock, StringComparer.OrdinalIgnoreCase).ToList();
        if (both.Count > 0)
        {
            return Invalid($"Can't both unlock and lock {string.Join(", ", both)}.", appId);
        }

        var hasChanges = unlock.Count > 0 || @lock.Count > 0 || unlockAll;
        if (commit == false)
        {
            if (hasChanges)
            {
                return Invalid("--unlock, --lock and --unlock-all-cli need --commit to store the changes. " +
                    "To review changes in the window instead, use --unlock-all or --lock-all.", appId);
            }

            return new CommandLine
            {
                Mode = LaunchMode.GameWindow,
                AppId = appId,
                UnlockAllPreset = unlockAllPreset,
                LockAllPreset = lockAllPreset,
            };
        }

        if (unlockAllPreset || lockAllPreset)
        {
            return Invalid("--unlock-all and --lock-all only preset the window; with --commit use --unlock-all-cli " +
                "or --lock ID[,ID...].", appId);
        }

        if (unlockAll && @lock.Count > 0)
        {
            return Invalid("--unlock-all-cli can't be combined with --lock.", appId);
        }

        if (hasChanges == false)
        {
            return Invalid("--commit needs something to change: --unlock ID[,ID...], --lock ID[,ID...] or --unlock-all-cli.", appId);
        }

        return new CommandLine
        {
            Mode = LaunchMode.Commit,
            AppId = appId,
            Unlock = unlock,
            Lock = @lock,
            UnlockAll = unlockAll,
            Quiet = quiet,
            NoBackup = noBackup,
        };
    }

    private static bool IsHelp(string arg) =>
        arg is "--help" or "-h" or "-?" or "/?" or "/h" or "/help" ||
        string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase);

    private static CommandLine Invalid(string error, uint appId = 0) =>
        new() { Mode = LaunchMode.Invalid, AppId = appId, Error = error };
}
