namespace SAM.App.Views;

/// <summary>
/// A window that can apply the <c>--unlock-all</c> / <c>--lock-all</c> launch flags after it's
/// open, when a second launch for the same App ID forwards them to the running copy.
/// </summary>
public interface ILaunchPresetTarget
{
    void ApplyLaunchPreset(bool unlockAll, bool lockAll);
}
