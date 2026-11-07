using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;

namespace Eden.Server.Launcher;

[Injectable(InjectionType.Singleton)]
internal sealed class LauncherMaintenance(LauncherStore store, TimeProvider clock) : IDisposable, IAsyncDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private readonly object _gate = new();
    private ITimer? _timer;
    private Task _pending = Task.CompletedTask;
    private bool _stopping;
    private Exception? _lastFailure;
    internal Exception? LastFailure => Volatile.Read(ref _lastFailure);
    internal Task Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _timer ??= clock.CreateTimer(_ => Queue(), null, Interval, Interval);
        }
    }

    private void Queue()
    {
        lock (_gate)
        {
            if (_stopping || !_pending.IsCompleted)
            {
                return;
            }
            using (ExecutionContext.SuppressFlow())
            {
                _pending = Task.Run(() =>
                {
                    try
                    {
                        store.Maintain();
                        Volatile.Write(ref _lastFailure, null);
                    }
                    catch (Exception error)
                    {
                        Volatile.Write(ref _lastFailure, error);
                        ServerLog.Console.Error($"Authentication cleanup failed; retrying next interval: {error.Message}");
                    }
                });
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_gate)
        {
            _stopping = true;
            _timer?.Dispose();
            pending = _pending;
        }
        // Finish any admitted batch before the host disposes its resident world.
        await pending.ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
