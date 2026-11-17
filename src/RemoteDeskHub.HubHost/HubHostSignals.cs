using System.Threading;

namespace RemoteDeskHub.HubHost;

/// <summary>
/// Named events used by the desktop console to discover, start and stop the headless hub host.
/// The objects live in the current Windows session, so a per-user console and hub host can see
/// them without elevation.
/// </summary>
public sealed class HubHostSignals : IDisposable
{
    public const string ReadyEventName = @"Local\RemoteDeskHub.HubHost.Ready";
    public const string StopEventName = @"Local\RemoteDeskHub.HubHost.Stop";

    private readonly EventWaitHandle _ready;
    private readonly EventWaitHandle _stop;

    public HubHostSignals()
    {
        _ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName);
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);

        // A stale event may have survived a forcibly terminated host. Reset both before the new
        // host advertises itself.
        _ready.Reset();
        _stop.Reset();
    }

    public void MarkReady() => _ready.Set();

    public void MarkStopped() => _ready.Reset();

    public async Task WaitForStopAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state => ((EventWaitHandle)state!).Set(), _stop);
        await Task.Run(() => _stop.WaitOne(), CancellationToken.None);
    }

    public void Dispose()
    {
        _ready.Dispose();
        _stop.Dispose();
    }
}
