using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Infrastructure.Services;

public sealed class AppEvents : IAppEvents
{
    public event EventHandler<Guid>? ComputerChanged;
    public event EventHandler<AppNotification>? NotificationRaised;
    public event EventHandler? DataChanged;

    public void RaiseComputerChanged(Guid computerId) => ComputerChanged?.Invoke(this, computerId);
    public void RaiseNotification(AppNotification notification) => NotificationRaised?.Invoke(this, notification);
    public void RaiseDataChanged() => DataChanged?.Invoke(this, EventArgs.Empty);
}
