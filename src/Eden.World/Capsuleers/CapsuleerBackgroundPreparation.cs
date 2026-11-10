using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
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
    private readonly Queue<int> _systems = new();
    private readonly HashSet<int> _regions = [];
    private HashSet<int> _pending = [];
    private int _total;
    private int _completed;
    private int _deferred;

    public Task Changed => activity.MembershipChanged;

    public CapsuleerBackgroundTurn PrepareNext(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var scope = new CatalogReadScope().Begin();
            var occupied = activity.Read().ActiveSystems;
            ActivateRegions(occupied);
            int? selected = occupied.Where(_pending.Contains).Order().Cast<int?>().FirstOrDefault();
            if (selected is null)
            {
                while (_systems.TryPeek(out int next) && !_pending.Contains(next))
                {
                    _systems.Dequeue();
                }
                selected = _systems.TryPeek(out int system) ? system : null;
            }
            if (selected is not int target)
            {
                return new(new(_completed, _total, _deferred), null, true);
            }
            try
            {
                var turn = _provisioner.PrepareBackgroundSystem(target, synchronization, cancellationToken);
                // Register only this committed community, rather than reloading every saved pilot on the next wake.
                scheduler.PopulationPrepared(target);
                _pending.Remove(target);
                _completed++;
                _deferred += turn.PendingCommunities;
                return new(new(_completed, _total, _deferred), target, _pending.Count == 0);
            }
            catch (CapsuleerBlueprintNotPreparedException)
            {
                // A concurrent identity reservation changed the chosen fit. Revalidate outside the owner next turn.
                return new(new(_completed, _total, _deferred), target, false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Leave its saved fingerprint pending, and continue the other activated systems.
                _pending.Remove(target);
                _completed++;
                _deferred++;
                return new(new(_completed, _total, _deferred), target, _pending.Count == 0) { Failure = error.Message };
            }
        }
    }

    private void ActivateRegions(IReadOnlyList<int> occupied)
    {
        if (occupied.Count == 0)
        {
            return;
        }
        using (CatalogDbContext catalog = new(database.CatalogPath))
        {
            foreach (int entered in occupied)
            {
                int region = catalog.RequireSystem(entered).RegionId;
                if (!_regions.Add(region))
                {
                    continue;
                }
                var systems = catalog
                    .Definitions.Systems.Values.Where(system => system.RegionId == region)
                    .OrderBy(system => system.Id == entered ? 0 : 1)
                    .ThenBy(system => system.Id);
                foreach (var system in systems)
                {
                    _pending.Add(system.Id);
                    _systems.Enqueue(system.Id);
                    _total++;
                }
            }
        }
    }

    public void Dispose() => _provisioner.Dispose();
}
