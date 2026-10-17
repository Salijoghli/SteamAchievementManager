namespace SAM.App.Services;

/// <summary>Windows toast notifications. No-ops when AppSettings.ShowNotifications is off or unavailable.</summary>
public sealed class NotificationService(AppSettings settings)
{
    public void Show(string title, string message) => throw new NotImplementedException("TODO(infra)");
}
