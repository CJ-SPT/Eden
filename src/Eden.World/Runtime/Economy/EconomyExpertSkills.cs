using System.Collections.Frozen;
using Eden.Data.World.Skills;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Skills;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyExpertSkills(long CatalogGeneration, long NextExpiry, FrozenDictionary<int, ExpertVirtualSkill> Grants)
{
    internal static readonly EconomyExpertSkills Empty = new(0, long.MaxValue, FrozenDictionary<int, ExpertVirtualSkill>.Empty);

    internal static EconomyExpertSkills Project(
        CatalogDbContext catalog,
        IEnumerable<(int TypeId, long ExpiresAt)> installs,
        long now,
        long generation
    )
    {
        Dictionary<int, ExpertVirtualSkill> grants = [];
        long next = long.MaxValue;
        foreach (var (typeId, expires) in installs)
        {
            if (expires <= now)
            {
                continue;
            }
            next = Math.Min(next, expires);
            if (ExpertSystemCatalog.Read(catalog, typeId) is not { } system)
            {
                continue;
            }
            foreach (var grant in system.Skills)
            {
                if (!grants.TryGetValue(grant.TypeId, out var current) || grant.Level > current.Level)
                {
                    grants[grant.TypeId] = grant;
                }
            }
        }
        return new(generation, next, grants.ToFrozenDictionary());
    }
}

internal sealed partial class ResidentEconomy
{
    private readonly Dictionary<long, EconomyExpertSkills> _expertSkills = [];

    internal long ExpertSkillDeadline(long pilot, long now) => ExpertSkills(pilot, now).NextExpiry;

    internal EconomyExpertSkills ExpertSkills(long pilot, long now, CatalogDbContext? source = null)
    {
        if (
            _expertSkills.TryGetValue(pilot, out var cached)
            && cached.NextExpiry > now
            && (ReferenceEquals(cached, EconomyExpertSkills.Empty) || cached.CatalogGeneration == _database.Catalog.Identity.Generation)
        )
        {
            return cached;
        }
        var table = (EconomyTable<SavedExpertSystem, EconomySavedExpertSystem>)_tables[typeof(SavedExpertSystem)];
        var installs = table.Indexed(nameof(SavedExpertSystem.PilotId), pilot);
        if (installs.Count == 0)
        {
            _expertSkills[pilot] = EconomyExpertSkills.Empty;
            return EconomyExpertSkills.Empty;
        }
        long generation = _database.Catalog.Identity.Generation;
        using var catalog = source is null ? new CatalogDbContext(_database.CatalogPath) : null;
        var entries = installs.Select(table.Snapshot).OfType<EconomySavedExpertSystem>().Select(entry => (entry.TypeId, entry.ExpiresAt));
        var current = EconomyExpertSkills.Project(source ?? catalog!, entries, now, generation);
        _expertSkills[pilot] = current;
        return current;
    }
}

internal sealed partial class EconomyTransaction
{
    private long? _expertSkillTime;

    private long ExpertSkillTime => _expertSkillTime ??= owner!.SimulationTime;

    internal IReadOnlyDictionary<int, ExpertVirtualSkill> ExpertSkills(CatalogDbContext catalog, long pilot)
    {
        if (owner is null)
        {
            return EconomyExpertSkills.Empty.Grants;
        }
        long now = ExpertSkillTime;
        if (_sets.TryGetValue(typeof(SavedExpertSystem), out var installs) && installs.Prepare())
        {
            var entries = Filter<SavedExpertSystem>(entry => entry.PilotId == pilot).AsEnumerable().Select(r => (r.TypeId, r.ExpiresAt));
            return EconomyExpertSkills.Project(catalog, entries, now, generation: 0).Grants;
        }
        return store.ExpertSkills(pilot, now, catalog).Grants;
    }
}
