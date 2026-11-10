using Eden.Data.World.Exploration;
using Eden.Data.World.Mining;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed class AgencyDirectory(WorldDatabase database) : IAgencyDirectory
{
    private long _catalogGeneration = -1;
    private IReadOnlyDictionary<int, int> _iceSystems = new Dictionary<int, int>();

    public AgencyIceDirectory Ice(long now)
    {
        return LiveWorld.Execute(database.Path, () => IceOwned(now), nameof(Ice));
    }

    private AgencyIceDirectory IceOwned(long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        lock (database.MotionLock)
        {
            if (_catalogGeneration != database.Catalog.Identity.Generation)
            {
                using CatalogDbContext catalog = new(database.CatalogPath);
                // Match MiningSiteStore's admission. A count advertises one field per
                // system, even when several pinned templates are eligible there.
                _iceSystems = MiningSiteCatalog
                    .Sites.Where(s =>
                        s.Family == MiningFamily.Ice
                        && s.Deposits.All(d => catalog.FittingType(d.TypeId)?.GroupId == MiningDefinitions.IceGroup)
                    )
                    .SelectMany(s => s.Systems!)
                    .Distinct()
                    .Where(id => catalog.FindSystem(id) is not null && catalog.CelestialsInSystem(id).Any())
                    .ToDictionary(id => id, _ => 1);
                _catalogGeneration = database.Catalog.Identity.Generation;
            }
            using WorldDbContext world = new(database.Path);
            var latest = world
                .Set<SavedMiningSite>()
                .AsNoTracking()
                .Where(s =>
                    s.Family == MiningFamily.Ice
                    && !world.Set<SavedMiningSite>().Any(n => n.SystemId == s.SystemId && n.Family == s.Family && n.Id > s.Id)
                )
                .OrderBy(s => s.SystemId)
                .ToArray();
            var counts = _iceSystems.ToDictionary();
            List<AnomalySystemSites> known = [];
            foreach (var site in latest)
            {
                if (!counts.ContainsKey(site.SystemId))
                {
                    continue;
                }
                if (site.ReplaceAt is long due)
                {
                    counts[site.SystemId] = due <= now ? 1 : 0;
                    continue;
                }
                var profile = MiningSiteCatalog.Sites.FirstOrDefault(s =>
                    s.DungeonId == site.DungeonId && s.Family == MiningFamily.Ice && s.Systems!.Contains(site.SystemId)
                );
                if (profile is null || site.ExpiresAt <= now)
                {
                    counts[site.SystemId] = 0;
                    continue;
                }
                CombatAnomaly contact = new(
                    site.Id,
                    ScannerTargetIds.FromMiningSequence(site.Id - MiningSiteRules.FirstSiteId),
                    site.SystemId,
                    site.DungeonId,
                    profile.NameId,
                    profile.Difficulty,
                    new(site.X, site.Y, site.Z),
                    true,
                    [],
                    FactionId: 0,
                    Mining: MiningFamily.Ice
                );
                known.Add(new(site.SystemId, [contact]));
            }
            return new(known, counts.OrderBy(p => p.Key).Select(p => new AgencySystemCount(p.Key, p.Value)).ToArray());
        }
    }

    public IReadOnlyList<AgencySystemCount> Signatures(long now)
    {
        return LiveWorld.Execute(database.Path, () => SignaturesOwned(now), nameof(Signatures));
    }

    private IReadOnlyList<AgencySystemCount> SignaturesOwned(long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        lock (database.MotionLock)
        {
            using WorldDbContext world = new(database.Path);
            List<int> systems = [];
            if (database.Settings.NpcPopulation.Pirates)
            {
                systems.AddRange(
                    world
                        .Set<WorldCombatAnomaly>()
                        .Where(s =>
                            s.IsSignature
                            && s.CompletedAt == null
                            && !world
                                .Set<WorldCombatAnomaly>()
                                .Any(n => n.SystemId == s.SystemId && n.Slot == s.Slot && n.IsSignature && n.Id > s.Id)
                        )
                        .Select(s => s.SystemId)
                );
                systems.AddRange(
                    world
                        .Set<SavedExplorationSite>()
                        .Where(s =>
                            s.ReplaceAt == null
                            && s.ExpiresAt > now
                            && !world.Set<SavedExplorationSite>().Any(n => n.SystemId == s.SystemId && n.Kind == s.Kind && n.Id > s.Id)
                        )
                        .Select(s => s.SystemId)
                );
            }
            systems.AddRange(
                world
                    .Set<SavedMiningSite>()
                    .Where(s =>
                        s.Family == MiningFamily.Gas
                        && s.ReplaceAt == null
                        && s.ExpiresAt > now
                        && !world.Set<SavedMiningSite>().Any(n => n.SystemId == s.SystemId && n.Family == s.Family && n.Id > s.Id)
                    )
                    .Select(s => s.SystemId)
            );
            // Remote signatures intentionally reveal only counts. These reads neither
            // generate sites nor expose unscanned IDs, positions, types or rewards.
            return systems.GroupBy(id => id).OrderBy(g => g.Key).Select(g => new AgencySystemCount(g.Key, g.Count())).ToArray();
        }
    }
}
