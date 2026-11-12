using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
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

    public Task Changed => activity.MembershipChanged;

    public CapsuleerBackgroundTurn PrepareNext(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var occupied = activity.Read().ActiveSystems;
            var progress = new CapsuleerPreparationProgress(
                occupied.Count(_completed.ContainsKey),
                occupied.Count,
                occupied.Sum(system => _completed.GetValueOrDefault(system))
            );
            if (scheduler.NextInboundPreparation() is { } inbound)
            {
                try
                {
                    long? pilot = _provisioner.PrepareBackgroundInbound(inbound, synchronization, cancellationToken);
                    scheduler.InboundPrepared(inbound, pilot);
                }
                catch (CapsuleerBlueprintNotPreparedException)
                {
                    // Re-plan a changed reservation on the next bounded turn.
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    scheduler.InboundPrepared(inbound, null);
                    return new(progress, inbound.Gate.ObserverSystemId, false) { Failure = error.Message };
                }
                return new(progress, inbound.Gate.ObserverSystemId, false);
            }
            int target = occupied.FirstOrDefault(system => !_completed.ContainsKey(system));
            if (target == 0)
            {
                return new(progress, null, true);
            }
            try
            {
                var turn = _provisioner.PrepareBackgroundSystem(
                    target,
                    synchronization,
                    () => activity.Read().ActiveSystems.Contains(target),
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
                return turn.HasMoreWork ? new(progress, target, false) : Complete(target, turn.PendingCommunities, progress);
            }
            catch (CapsuleerBlueprintNotPreparedException)
            {
                return new(progress, target, false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return Complete(target, 1, progress) with { Failure = error.Message };
            }
        }
    }

    private CapsuleerBackgroundTurn Complete(int system, int deferred, CapsuleerPreparationProgress progress)
    {
        _completed[system] = deferred;
        var updated = progress with
        {
            CompletedSystems = progress.CompletedSystems + 1,
            DeferredSystems = progress.DeferredSystems + deferred,
        };
        return new(updated, system, updated.CompletedSystems == updated.TotalSystems);
    }

    public void Dispose() => _provisioner.Dispose();
}
