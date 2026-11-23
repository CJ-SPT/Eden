using System.Collections.Frozen;
using Eden.Gameplay.Dogma;
using Eden.World.Fittings;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private long _fitGeneration;
    private readonly Dictionary<long, long> _pilotFits = [];

    internal string FitRevision(long pilot, long now)
    {
        long deadline = ExpertSkillDeadline(pilot, now);
        long revision = _pilotFits.GetValueOrDefault(pilot);
        long generation = _database.Catalog.Identity.Generation;
        return deadline == long.MaxValue
            ? $"resident:{_fitGeneration}:{revision}:{generation}:{pilot}"
            : $"resident:{_fitGeneration}:{revision}:{generation}:{pilot}:{deadline}";
    }

    internal string? PersistedFitRevision(long pilot)
    {
        long now = _owner.SimulationTime;
        // Existing SQL fitting paths calculate trained levels and share the resident cache only without active grants.
        return ExpertSkillDeadline(pilot, now) == long.MaxValue ? FitRevision(pilot, now) : null;
    }

    private void InvalidateFits(IReadOnlyCollection<Eden.World.Runtime.Combat.CombatRowChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.Table == "WorldExpertSystem")
            {
                _expertSkills.Remove(change.Id);
            }
            else if (change.Table == "schema")
            {
                _expertSkills.Clear();
            }
        }
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
        // Fitting reads can open more sets while resolving a module's owner or
        // environment. Inspect only the current sets that can invalidate a fit.
        foreach (var set in _sets.Where(entry => s_fitInputs.Contains(entry.Key)).Select(entry => entry.Value).ToArray())
        {
            if (set.Prepare() && set.Changes.Any(c => ResidentEconomy.IsFitDependency(c.Table)))
            {
                return true;
            }
        }
        return false;
    }

    internal override string? FitRevision(long pilot) =>
        previewCatalog is not null || PendingFitChanges() ? null : store.FitRevision(pilot, ExpertSkillTime);

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
        if (_fits.Count > 0 || _snapshots.Count > 0)
        {
            var cache = ResidentFitCache.Find(WorldPath);
            if (_fits.Count > 0)
            {
                cache?.Publish(_fits);
            }
            if (_snapshots.Count > 0)
            {
                cache?.PublishSnapshots(_snapshots);
            }
        }
    }
}
