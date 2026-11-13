using System.Collections.Frozen;
using Eden.Gameplay.Dogma;
using Eden.World.Fittings;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private long _fitGeneration;
    private readonly Dictionary<long, long> _pilotFits = [];

    internal string FitRevision(long pilot) =>
        $"resident:{_fitGeneration}:{_pilotFits.GetValueOrDefault(pilot)}:{_database.Catalog.Identity.Generation}:{pilot}";

    private void InvalidateFits(IReadOnlyCollection<Eden.World.Runtime.Combat.CombatRowChange> changes)
    {
        foreach (long pilot in changes.Where(c => c.Table == "WorldFitRevision").Select(c => c.Id).Distinct())
        {
            _pilotFits[pilot] = _pilotFits.GetValueOrDefault(pilot) + 1;
        }
        if (
            changes.Any(c =>
                c.Table is "WorldConfigurationState" or "schema"
                || !c.Resident && c.Table is "WorldNpcEwar" or "WorldPlayerEwar" or "WorldRemoteAssistance" or "WorldCommandBurst"
            )
        )
        {
            _fitGeneration++;
        }
    }

    internal static bool IsFitDependency(string table) => table is "WorldFitRevision" or "WorldConfigurationState" or "schema";
}

internal sealed partial class EconomyTransaction
{
    private readonly Dictionary<FitCacheKey, FitCacheEntry> _fits = [];
    private readonly Dictionary<(long Pilot, long Ship), FittingSnapshotEntry> _snapshots = [];

    private bool PendingFitChanges()
    {
        foreach (var set in _sets.Values.ToArray())
        {
            if (set.Prepare() && set.Changes.Any(c => ResidentEconomy.IsFitDependency(c.Table)))
            {
                return true;
            }
        }
        return false;
    }

    internal override string? FitRevision(long pilot) => PendingFitChanges() ? null : store.FitRevision(pilot);

    internal override Dictionary<DogmaAttributeId, double>? CachedFit(FitCacheKey key, string? revision) =>
        revision is null ? null
        : _fits.TryGetValue(key, out var pending) && pending.Revision == revision ? pending.Values.ToDictionary()
        : ResidentFitCache.Find(WorldPath)?.Read(key, revision);

    internal override void StageFit(FitCacheKey key, string? revision, IReadOnlyDictionary<DogmaAttributeId, double> values)
    {
        if (revision is not null)
        {
            _fits[key] = new(revision, values.ToFrozenDictionary());
        }
    }

    internal override FittingSnapshot? CachedFittingSnapshot(long pilot, long ship, string? revision) =>
        revision is null ? null
        : _snapshots.TryGetValue((pilot, ship), out var pending) && pending.Revision == revision ? pending.Snapshot
        : ResidentFitCache.Find(WorldPath)?.ReadSnapshot(pilot, ship, revision);

    internal override void StageFittingSnapshot(long pilot, long ship, string? revision, FittingSnapshot snapshot)
    {
        if (revision is not null)
        {
            _snapshots[(pilot, ship)] = new(revision, snapshot);
        }
    }

    private void PublishFits()
    {
        var cache = ResidentFitCache.Find(WorldPath);
        cache?.Publish(_fits);
        cache?.PublishSnapshots(_snapshots);
    }
}
