using System.Collections.Concurrent;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed class CombatAnomalyStore(WorldDatabase database) : ICombatAnomalies
{
    private static readonly ConcurrentDictionary<string, long> s_versions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CombatAnomaly>? _sites;
    private long _version = -1;
    private long _catalogGeneration = -1;
    private long _deadline = long.MaxValue;

    internal static void Invalidate(WorldDbContext world) => s_versions.AddOrUpdate(world.WorldPath, 1, (_, old) => old + 1);

    public IReadOnlyList<CombatAnomaly> List(int systemId, long now)
    {
        if (systemId != CombatAnomalyRules.SystemId)
        {
            return [];
        }
        lock (database.MotionLock)
        {
            long version = s_versions.GetValueOrDefault(database.Path);
            long catalogGeneration = database.Catalog.Identity.Generation;
            if (_sites is not null && version == _version && catalogGeneration == _catalogGeneration && now < _deadline)
            {
                return _sites;
            }
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            // Small test catalogs and older reduced catalogs can omit dungeon data.
            if (catalog.Read(CatalogData.Client.Dungeons, (long)CombatAnomalyRules.DungeonId) is null)
            {
                _sites = [];
                _catalogGeneration = catalogGeneration;
                _version = version;
                return _sites;
            }
            using var transaction = world.Database.BeginTransaction();
            var latest = world.Set<WorldCombatAnomaly>().OrderByDescending(s => s.Id).FirstOrDefault();
            if (latest is null || latest.ReplaceAt <= now)
            {
                var system = catalog.RequireSystem(systemId);
                // Eden placement policy, a fixed offset from the first catalog celestial.
                var sun = catalog.CelestialsInSystem(system.Id).OrderBy(c => c.Id).First();
                long id = latest is null ? CombatAnomalyRules.FirstSiteId : checked(latest.Id + 1);
                latest = new()
                {
                    Id = id,
                    SystemId = systemId,
                    X = sun.X + CombatAnomalyRules.PlacementOffset,
                    Y = sun.Y + CombatAnomalyRules.PlacementOffset,
                    Z = sun.Z + CombatAnomalyRules.PlacementOffset,
                };
                world.Add(latest);
                world.SaveChanges();
            }
            transaction.Commit();
            _deadline = latest.ReplaceAt ?? long.MaxValue;
            _sites = latest.CompletedAt is null ? [Read(catalog, latest)] : [];
            _version = version;
            _catalogGeneration = catalogGeneration;
            return _sites;
        }
    }

    private static CombatAnomaly Read(CatalogDbContext catalog, WorldCombatAnomaly site)
    {
        var dungeon = catalog.Read(CatalogData.Client.Dungeons, (long)CombatAnomalyRules.DungeonId)!;
        var room = dungeon.Rooms!.Single().Value;
        var origin = new SpacePosition(site.X, site.Y, site.Z);
        var entry = room.Objects!.Values.Single(o => o.ObjectID == dungeon.EntryObjectID).Position!;
        var offset = origin - new SpacePosition(entry.X!.Value, entry.Y!.Value, entry.Z!.Value);
        // The radio telescope is the visible landmark. Invisible spawners, audio
        // objects and client environment effects have no ordinary ball adapter.
        var objects = room
            .Objects.Values.Where(o => o.TypeID == CombatAnomalyRules.TelescopeTypeId)
            .Select(o =>
            {
                var type = catalog.RequireItem(checked((int)o.TypeID!.Value));
                var p = o.Position!;
                return new StaticSpaceObject(
                    CombatAnomalyRules.FirstObjectId + site.Id - CombatAnomalyRules.FirstSiteId,
                    type.Id,
                    type.GroupId,
                    type.CategoryId,
                    NpcEncounterDefinitions.GuristasCorporation,
                    offset + new SpacePosition(p.X!.Value, p.Y!.Value, p.Z!.Value),
                    (float)(type.Radius ?? 0)
                );
            })
            .ToArray();
        return new(
            site.Id,
            $"EDN-{site.Id - CombatAnomalyRules.FirstSiteId + 1:000}",
            site.SystemId,
            CombatAnomalyRules.DungeonId,
            checked((int)dungeon.DungeonNameID!.Value),
            checked((int)dungeon.Difficulty!.Value),
            origin,
            site.Wave >= 0,
            objects
        );
    }

    public void Arrive(int systemId, SpacePosition position, long now)
    {
        lock (database.MotionLock)
        {
            var contact = List(systemId, now).SingleOrDefault();
            if (contact is null || contact.Started || position.DistanceTo(contact.Position) > CombatAnomalyRules.ArrivalRange)
            {
                return;
            }
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            var site = world.Set<WorldCombatAnomaly>().Find(contact.Id)!;
            if (site.Wave < 0 && site.CompletedAt is null)
            {
                site.Wave = 0;
                Spawn(world, catalog, site, now);
                world.SaveChanges();
            }
            transaction.Commit();
            Invalidate(world);
        }
    }

    internal static void Killed(WorldDbContext world, CatalogDbContext catalog, WorldNpc npc, long now)
    {
        if (npc.AnchorKind != NpcAnchorKind.Anomaly)
        {
            return;
        }
        var site = world.Set<WorldCombatAnomaly>().Find(npc.EncounterId) ?? throw new InvalidDataException("Anomaly NPC has no site.");
        // Materialization reuses tracked entities, including other deaths in this transaction.
        bool remaining = world
            .Set<WorldNpc>()
            .Where(n => n.EncounterId == site.Id && n.Id != npc.Id)
            .ToArray()
            .Any(n => n.DestroyedAt is null && n.RetiredAt is null);
        if (remaining || site.CompletedAt is not null)
        {
            return;
        }
        if (site.Wave == 3)
        {
            site.CompletedAt = now;
            site.ReplaceAt = checked(now + CombatAnomalyRules.ReplacementSeconds * TimeSpan.TicksPerSecond);
        }
        else
        {
            site.Wave++;
            Spawn(world, catalog, site, now);
        }
        // Eviction publishes no uncommitted state. The next reader reloads after
        // the owner releases the transaction, including after rollback.
        Invalidate(world);
    }

    private static void Spawn(WorldDbContext world, CatalogDbContext catalog, WorldCombatAnomaly site, long now)
    {
        var dungeon = catalog.Read(CatalogData.Client.Dungeons, (long)CombatAnomalyRules.DungeonId)!;
        var spawner = dungeon.Rooms!.Single().Value.Objects!.Values.Single(o => o.Entities is not null).Position!;
        var entry = dungeon.Rooms!.Single().Value.Objects!.Values.Single(o => o.ObjectID == dungeon.EntryObjectID).Position!;
        var origin = new SpacePosition(
            site.X + spawner.X!.Value - entry.X!.Value,
            site.Y + spawner.Y!.Value - entry.Y!.Value,
            site.Z + spawner.Z!.Value - entry.Z!.Value
        );
        var roster = CombatAnomalyRules.Wave(site.Id, site.Wave);
        for (int slot = 0; slot < roster.Count; slot++)
        {
            var profile = new NpcProfileCatalog().Read(catalog, roster[slot]);
            var p = origin + new SpacePosition(slot * NpcSpawnRules.MemberSpacing, 0, 0);
            world.Add(
                new WorldNpc
                {
                    Id = checked(CombatAnomalyRules.FirstNpcId + (site.Id - CombatAnomalyRules.FirstSiteId) * 100 + site.Wave * 10 + slot),
                    SystemId = site.SystemId,
                    TypeId = roster[slot],
                    Role = NpcRole.Pirate,
                    AnchorKind = NpcAnchorKind.Anomaly,
                    EncounterId = site.Id,
                    SpawnSlot = slot,
                    CorporationId = NpcEncounterDefinitions.GuristasCorporation,
                    FactionId = NpcEncounterDefinitions.GuristasFaction,
                    HomeX = p.X,
                    HomeY = p.Y,
                    HomeZ = p.Z,
                    X = p.X,
                    Y = p.Y,
                    Z = p.Z,
                    ShieldCharge = profile.Attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
                    UpdatedAt = now,
                    MotionAt = now,
                }
            );
        }
    }
}
