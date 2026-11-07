using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class MarketMaintenance(
    IPersonalMarket market,
    TimeProvider clock,
    IResourceRenewal? resources = null,
    WorldDatabase? database = null
) : IDisposable, IAsyncDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private ITimer? _timer;
    private Task _pending = Task.CompletedTask;
    private bool _stopping;
    private Exception? _lastFailure;
    private DateTimeOffset _nextResourceCheck;
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
            var interval = database is null ? Interval : TimeSpan.FromMilliseconds(database.Settings.Simulation.MarketPollMilliseconds);
            _timer ??= clock.CreateTimer(_ => Queue(), null, interval, interval);
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
                        if (market.NextExpiry <= clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
                        {
                            market.Expire();
                        }
                        if (resources is not null && clock.GetUtcNow() >= _nextResourceCheck)
                        {
                            if (resources.Renew() == 0)
                            {
                                _nextResourceCheck = clock.GetUtcNow().AddSeconds(database?.Settings.Simulation.ResourceCheckSeconds ?? 60);
                            }
                        }
                        Volatile.Write(ref _lastFailure, null);
                    }
                    catch (Exception error)
                    {
                        Volatile.Write(ref _lastFailure, error);
                        ServerLog.Console.Error($"Economic maintenance failed; retrying next interval: {error.Message}");
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
