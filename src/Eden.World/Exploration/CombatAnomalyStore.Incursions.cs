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
    private const int IncursionSlot = 102;
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
        if (focus?.Systems.Any(s => s.SystemId == systemId && s.SceneType == IncursionScene.Vanguard) != true)
        {
            return [];
        }
        if (IncursionSiteCatalog.Read(catalog) is not { } definition)
        {
            return [];
        }
        using var transaction = live.Economy.Begin(new(systems: [systemId]));
        WorldAction world = transaction;
        long? id = world
            .Filter<WorldCombatAnomaly>(s => s.IncursionTaleId == focus.TaleId && s.SystemId == systemId)
            .Select(s => (long?)s.Id)
            .OrderByDescending(id => id)
            .FirstOrDefault();
        var site = id is long previous ? world.Set<WorldCombatAnomaly>().Find(previous) : null;
        if (site is null || site.ReplaceAt <= now)
        {
            var anchor = catalog.CelestialsInSystem(systemId).MinBy(c => c.Id);
            if (anchor is null)
            {
                return [];
            }
            double offset = CombatAnomalyRules.PlacementOffset * IncursionPlacementSlot;
            site = new()
            {
                Id = NextSiteId(world),
                SystemId = systemId,
                Slot = IncursionSlot,
                DungeonId = definition.Encounter.DungeonId,
                DefinitionVersion = AnomalyEncounterRules.CurrentDefinitionVersion,
                IncursionTaleId = focus.TaleId,
                X = anchor.X + offset,
                Y = anchor.Y - offset,
                Z = anchor.Z + offset,
            };
            IncursionStore.Track(world, focus);
            world.Add(site);
        }
        transaction.Commit();
        IReadOnlyList<CombatAnomaly> contacts = site.CompletedAt is null ? [Contact(site, definition.Encounter)] : [];
        _incursionViews[systemId] = new(version, generation, Math.Min(site.ReplaceAt ?? long.MaxValue, focus.EndsAt), contacts);
        return contacts;
    }

    private static CombatAnomalyDefinition SiteDefinition(CatalogDbContext catalog, WorldCombatAnomaly site)
    {
        var definition = site.IncursionTaleId.HasValue
            ? IncursionSiteCatalog.Read(catalog)?.Encounter
            : CombatAnomalyCatalog.Find(catalog, site.DungeonId);
        return definition ?? throw new InvalidDataException($"Anomaly definition {site.DungeonId} is missing.");
    }

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
        var definition = IncursionSiteCatalog.Read(catalog) ?? throw new InvalidDataException("The Incursion site profile is unavailable.");
        var origin = DedOrigin(site);
        MissionGate gate = new(
            gateId,
            origin + definition.Gate.Position,
            definition.Gate.Radius,
            origin + IncursionSiteCatalog.CombatOffset,
            false
        )
        {
            Access = definition.Access,
            DungeonObjectId = definition.Gate.Id,
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
