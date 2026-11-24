using System.Collections.Concurrent;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal sealed partial class CombatAnomalyStore
{
    private const int VanguardIncursionSlot = 102;
    private const int ScoutIncursionSlot = 103;
    private const int AssaultIncursionSlot = 104;
    private const int HeadquartersIncursionSlot = 105;
    private const int MothershipIncursionSlot = 106;
    private const int IncursionPlacementSlot = 25;

    private sealed record IncursionSiteView(long Version, long Catalog, long Deadline, IReadOnlyList<CombatAnomaly> Sites);

    private readonly ConcurrentDictionary<int, IncursionSiteView> _incursionViews = [];

    public IReadOnlyList<CombatAnomaly> Incursions(int systemId, long now)
    {
        if (!IncursionStore.MayHaveSite(systemId, now))
        {
            return [];
        }
        return LiveWorld.Execute(
            database.Path,
            systemId,
            () =>
            {
                using var ownership = LiveWorld.Find(database.Path)?.Enter();
                using var movement = database.EnterMovement();
                return IncursionsOwned(systemId, now);
            },
            nameof(Incursions)
        );
    }

    private IReadOnlyList<CombatAnomaly> IncursionsOwned(int systemId, long now)
    {
        if (!IncursionStore.MayHaveSite(systemId, now) || LiveWorld.Find(database.Path) is not { } live)
        {
            return [];
        }
        long version = Version(systemId);
        long generation = database.Catalog.Identity.Generation;
        if (
            _incursionViews.TryGetValue(systemId, out var view)
            && view.Version == version
            && view.Catalog == generation
            && now < view.Deadline
        )
        {
            return view.Sites;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var focus = live.Economy.Inspect(world => IncursionStore.ActiveFocus(world, catalog, now));
        if (focus?.Systems.SingleOrDefault(s => s.SystemId == systemId) is not { } system)
        {
            _incursionViews[systemId] = new(version, generation, IncursionRules.EndsAt(IncursionRules.TaleId(now)), []);
            return _incursionViews[systemId].Sites;
        }
        IncursionSiteKind[] kinds = system.SceneType switch
        {
            IncursionScene.Staging => [IncursionSiteKind.Scout],
            IncursionScene.Vanguard => [IncursionSiteKind.Vanguard],
            IncursionScene.Assault => [IncursionSiteKind.Assault],
            IncursionScene.Headquarters when focus.HasFinalEncounter => [IncursionSiteKind.Headquarters, IncursionSiteKind.Mothership],
            IncursionScene.Headquarters => [IncursionSiteKind.Headquarters],
            _ => [],
        };
        using var transaction = live.Economy.Begin(new(systems: [systemId]));
        WorldAction world = transaction;
        var previousSites = world
            .Filter<WorldCombatAnomaly>(s => s.IncursionTaleId == focus.TaleId && s.SystemId == systemId)
            .Select(s => new { s.DungeonId, s.Id })
            .AsEnumerable()
            .GroupBy(s => s.DungeonId)
            .ToDictionary(group => group.Key, group => group.Max(s => s.Id));
        List<CombatAnomaly> contacts = [];
        long deadline = focus.EndsAt;
        foreach (var kind in kinds)
        {
            if (IncursionSiteCatalog.Read(catalog, kind) is not { } definition)
            {
                continue;
            }
            int dungeonId = definition.Encounter.DungeonId;
            var site = previousSites.TryGetValue(dungeonId, out long previous) ? world.Set<WorldCombatAnomaly>().Find(previous) : null;
            if (site is null || (kind != IncursionSiteKind.Mothership && site.ReplaceAt <= now))
            {
                var anchor = catalog.CelestialsInSystem(systemId).MinBy(c => c.Id);
                if (anchor is null)
                {
                    continue;
                }
                int slot = IncursionSlot(kind);
                double offset = CombatAnomalyRules.PlacementOffset * (IncursionPlacementSlot + slot - VanguardIncursionSlot);
                site = new()
                {
                    Id = NextSiteId(world),
                    SystemId = systemId,
                    Slot = slot,
                    DungeonId = dungeonId,
                    DefinitionVersion = AnomalyEncounterRules.CurrentDefinitionVersion,
                    IncursionTaleId = focus.TaleId,
                    X = anchor.X + offset,
                    Y = anchor.Y - offset,
                    Z = anchor.Z + offset,
                };
                IncursionStore.Track(world, focus);
                world.Add(site);
            }
            while (site.CompletedAt is null && site.NextWaveAt is long due && due <= now)
            {
                site.NextWaveAt = null;
                NextWave(world, catalog, site, due);
            }
            if (site.CompletedAt is null)
            {
                contacts.Add(Contact(site, definition.Encounter));
            }
            if (kind != IncursionSiteKind.Mothership)
            {
                deadline = Math.Min(deadline, site.ReplaceAt ?? long.MaxValue);
            }
            deadline = Math.Min(deadline, site.NextWaveAt ?? long.MaxValue);
        }
        transaction.Commit();
        _incursionViews[systemId] = new(version, generation, deadline, contacts.ToArray());
        return _incursionViews[systemId].Sites;
    }

    private static int IncursionSlot(IncursionSiteKind kind) =>
        kind switch
        {
            IncursionSiteKind.Vanguard => VanguardIncursionSlot,
            IncursionSiteKind.Scout => ScoutIncursionSlot,
            IncursionSiteKind.Assault => AssaultIncursionSlot,
            IncursionSiteKind.Headquarters => HeadquartersIncursionSlot,
            IncursionSiteKind.Mothership => MothershipIncursionSlot,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static CombatAnomalyDefinition SiteDefinition(CatalogDbContext catalog, WorldCombatAnomaly site)
    {
        var definition = site.IncursionTaleId.HasValue
            ? IncursionDefinition(catalog, site)?.Encounter
            : CombatAnomalyCatalog.Find(catalog, site.DungeonId);
        return definition ?? throw new InvalidDataException($"Anomaly definition {site.DungeonId} is missing.");
    }

    private static IncursionSiteDefinition? IncursionDefinition(CatalogDbContext catalog, WorldCombatAnomaly site) =>
        IncursionSiteCatalog.Kind(site.DungeonId) is { } kind ? IncursionSiteCatalog.Read(catalog, kind) : null;

    internal static bool IncursionNpcActive(WorldAction world, WorldNpc npc, long now)
    {
        var site = world.Set<WorldCombatAnomaly>().Find(npc.EncounterId);
        return site is { CompletedAt: null, IncursionTaleId: long tale }
            && now >= IncursionRules.OpensAt(tale)
            && now < IncursionRules.EndsAt(tale);
    }

    internal static long IncursionNpcDeadline(WorldAction world, WorldNpc npc) =>
        world.Set<WorldCombatAnomaly>().Find(npc.EncounterId) is { CompletedAt: null, IncursionTaleId: long tale }
            ? IncursionRules.EndsAt(tale)
            : 0;

    internal static void RetireIncursion(WorldAction world, long taleId, long now)
    {
        foreach (var site in world.Filter<WorldCombatAnomaly>(s => s.IncursionTaleId == taleId && s.CompletedAt == null).ToArray())
        {
            site.CompletedAt = now;
            site.NextWaveAt = null;
            foreach (
                var npc in world.Filter<WorldNpc>(n => n.EncounterId == site.Id && n.DestroyedAt == null && n.RetiredAt == null).ToArray()
            )
            {
                npc.RetiredAt = now;
                if (world.Set<NpcAggression>().Find(npc.Id) is { } aggression)
                {
                    world.Remove(aggression);
                }
                foreach (var damage in world.Filter<SavedCombatDamage>(d => d.VictimId == npc.Id).ToArray())
                {
                    world.Remove(damage);
                }
            }
            Invalidate(world, site.SystemId);
        }
    }

    private static (WorldCombatAnomaly Site, Pilot Pilot, MissionGate Gate) ValidateIncursionGate(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long gateId,
        long now,
        WorldCombatAnomaly site
    )
    {
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown Incursion pilot.");
        var ship = pilot.ActiveShip;
        if (
            world is not EconomyTransaction
            || gateId != DedGateId(site.Id, 0)
            || site.CompletedAt.HasValue
            || ship.Position is not { } position
            || (position.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId) != site.SystemId
            || IncursionStore.ActiveFocus(world, catalog, now)?.TaleId != site.IncursionTaleId
        )
        {
            throw new InvalidDataException("The Incursion acceleration gate is unavailable in your current room.");
        }
        var definition = IncursionDefinition(catalog, site);
        if (definition?.Gate is not { } body)
        {
            throw new InvalidDataException("The Incursion site does not have this acceleration gate.");
        }
        var origin = DedOrigin(site);
        MissionGate gate = new(gateId, origin + body.Position, body.Radius, IncursionSiteCatalog.CombatCenter(site), false)
        {
            Access = definition.Access,
            DungeonObjectId = body.Id,
        };
        var type = catalog.RequireItem(ship.TypeId);
        if (!MissionGateAccessRules.AllowsShip(gate.Access, type.Id, type.GroupId, type.CategoryId, 0))
        {
            throw new InvalidDataException("This ship is not permitted through this acceleration gate.");
        }
        MissionGateRules.Validate(new(position.X, position.Y, position.Z), type.Radius ?? 0, type.GroupId, gate, true);
        return (site, pilot, gate);
    }
}
