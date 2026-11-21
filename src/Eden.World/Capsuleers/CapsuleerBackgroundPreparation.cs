using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerBackgroundPreparation(
    WorldDatabase database,
    TimeProvider clock,
    IWorldSynchronization synchronization,
    CapsuleerActivitySource activity,
    CapsuleerScheduler scheduler
) : ICapsuleerBackgroundPreparation, IDisposable
{
    private readonly Lock _gate = new();
    private readonly CapsuleerProvisioner _provisioner = new(database, clock);
    private readonly Dictionary<int, int> _completed = [];
    private readonly Dictionary<int, CapsuleerRetry> _systemRetries = [];
    private readonly Dictionary<CapsuleerScheduler.InboundPreparation, CapsuleerRetry> _inboundRetries = [];
    private readonly long _started = clock.GetTimestamp();
    private Queue<int>? _freshSystems;
    private int _lastSystem;

    private long NowTicks => clock.GetElapsedTime(_started).Ticks;

    public Task Changed => activity.MembershipChanged;

    public CapsuleerBackgroundTurn PrepareNext(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var human = activity.Read();
            var occupied = human.ActiveSystems;
            var policy = database.Settings.Capsuleers;
            if (occupied.Count == 0)
            {
                _inboundRetries.Clear();
                if (_freshSystems is null)
                {
                    _systemRetries.Clear();
                }
                return new(ReadProgress(occupied), null, true);
            }
            if (database.Settings.FreshUniverse && policy.Enabled && policy.PrepareUnvisitedSystems && _freshSystems is null)
            {
                var catalog = database.Catalog;
                _freshSystems = new(
                    catalog
                        .SystemPolitics.Where(entry =>
                            entry.Value.Security >= CrimewatchRules.HighSecurity
                            && !catalog.StationsInSystem(entry.Key).IsEmpty
                            && policy.CanWork(catalog, entry.Key)
                        )
                        .Select(entry => entry.Key)
                        .Order()
                );
            }
            var inboundRequests = scheduler.InboundPreparations();
            foreach (int system in _systemRetries.Keys.Where(system => _freshSystems is null && !occupied.Contains(system)).ToArray())
            {
                _systemRetries.Remove(system);
            }
            foreach (var request in _inboundRetries.Keys.Where(request => !inboundRequests.Contains(request)).ToArray())
            {
                _inboundRetries.Remove(request);
            }
            long now = NowTicks;
            var progress = ReadProgress(occupied);
            var inbound = inboundRequests.FirstOrDefault(request => Ready(_inboundRetries, request, now));
            if (inbound is not null)
            {
                try
                {
                    long? pilot = _provisioner.PrepareBackgroundInbound(inbound, synchronization, cancellationToken);
                    scheduler.InboundPrepared(inbound, pilot);
                    _inboundRetries.Remove(inbound);
                }
                catch (CapsuleerBlueprintNotPreparedException)
                {
                    Defer(_inboundRetries, inbound);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Keep this exact visitor cursor pending. Already committed visitors
                    // are reused by the normal provisioning operation on retry.
                    Defer(_inboundRetries, inbound);
                    return new(progress, inbound.Gate.ObserverSystemId, false) { Failure = error.Message };
                }
                return new(progress, inbound.Gate.ObserverSystemId, false);
            }
            int target = occupied
                .Where(system => !_completed.ContainsKey(system) && Ready(_systemRetries, system, now))
                .OrderBy(system => system <= _lastSystem)
                .ThenBy(system => system)
                .FirstOrDefault();
            bool foreground = target != 0;
            bool atCapacity = false;
            if (!foreground && _freshSystems is { Count: > 0 })
            {
                int cap = policy.ActivePilotCap;
                if (cap > 0)
                {
                    using var owner = synchronization.Enter("fresh population capacity");
                    atCapacity = LiveWorld.Find(database.Path)?.Economy.AdmittedCapsuleerCount >= cap;
                }
                if (!atCapacity)
                {
                    target = NextFreshSystem(now);
                }
            }
            if (target == 0)
            {
                long? retry = _systemRetries.Values.Concat(_inboundRetries.Values).Select(value => (long?)value.DueTicks).Min();
                bool complete =
                    progress.CompletedSystems == progress.TotalSystems
                    && inboundRequests.Count == 0
                    && (atCapacity || _freshSystems is null or { Count: 0 });
                return new(progress, null, complete)
                {
                    RetryLater = !complete,
                    RetryAfter = retry is long deadline ? TimeSpan.FromTicks(Math.Max(0, deadline - now)) : null,
                };
            }
            if (foreground)
            {
                _lastSystem = target;
            }
            try
            {
                var turn = _provisioner.PrepareBackgroundSystem(
                    target,
                    synchronization,
                    () => foreground ? activity.Read().ActiveSystems.Contains(target) : activity.Read().Active,
                    cancellationToken
                );
                if (turn is null)
                {
                    return new(progress, target, false) { RetryLater = true };
                }
                if (turn.CreatedPilotId is long pilot)
                {
                    scheduler.PopulationPrepared(target, pilot);
                }
                if (!turn.HasMoreWork)
                {
                    _completed[target] = turn.PendingCommunities;
                }
                _systemRetries.Remove(target);
                var updated = ReadProgress(occupied);
                return new(
                    updated,
                    target,
                    !turn.HasMoreWork
                        && updated.CompletedSystems == updated.TotalSystems
                        && inboundRequests.Count == 0
                        && _freshSystems is null or { Count: 0 }
                );
            }
            catch (CapsuleerBlueprintNotPreparedException)
            {
                Defer(_systemRetries, target);
                return new(ReadProgress(occupied), target, false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Defer(_systemRetries, target);
                return new(ReadProgress(occupied), target, false) { Failure = error.Message };
            }
        }
    }

    private int NextFreshSystem(long now)
    {
        var candidates = _freshSystems!;
        int remaining = candidates.Count;
        while (remaining-- > 0)
        {
            int candidate = candidates.Dequeue();
            if (_completed.ContainsKey(candidate))
            {
                continue;
            }
            candidates.Enqueue(candidate);
            if (Ready(_systemRetries, candidate, now))
            {
                return candidate;
            }
        }
        return 0;
    }

    private CapsuleerPreparationProgress ReadProgress(IReadOnlyList<int> occupied) =>
        new(
            occupied.Count(_completed.ContainsKey),
            occupied.Count,
            occupied.Sum(system => _completed.GetValueOrDefault(system)) + occupied.Count(_systemRetries.ContainsKey)
        );

    private static bool Ready<T>(Dictionary<T, CapsuleerRetry> retries, T key, long now)
        where T : notnull => !retries.TryGetValue(key, out var retry) || retry.DueTicks <= now;

    private void Defer<T>(Dictionary<T, CapsuleerRetry> retries, T key)
        where T : notnull
    {
        retries[key] = CapsuleerRetry.Next(retries.GetValueOrDefault(key), NowTicks);
    }

    public void Dispose() => _provisioner.Dispose();
}
