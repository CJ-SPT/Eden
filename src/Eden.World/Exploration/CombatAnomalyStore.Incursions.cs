using System.Collections.Concurrent;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Mining;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal sealed partial class CombatAnomalyStore
{
    private const int FirstIncursionSlot = 102;
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
        var focus = live.Economy.Inspect(world => IncursionStore.ActiveFocus(world, catalog, systemId, now));
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
        if (
            system.SceneType == IncursionScene.Headquarters
            && !focus.HasFinalEncounter
            && focus.FinalUnlockAt is long unlock
            && unlock > now
        )
        {
            deadline = Math.Min(deadline, unlock);
        }
        foreach (var definition in kinds.SelectMany(kind => IncursionSiteCatalog.Variants(catalog, kind, focus.Security)))
        {
            var kind = definition.Kind;
            int dungeonId = definition.Encounter.DungeonId;
            var site = previousSites.TryGetValue(dungeonId, out long previous) ? world.Set<WorldCombatAnomaly>().Find(previous) : null;
            if (site is null || (kind != IncursionSiteKind.Mothership && site.ReplaceAt <= now))
            {
                var bodies = catalog.CelestialsInSystem(systemId);
                var belt =
                    dungeonId == IncursionSiteCatalog.NationIndustrialProxy
                        ? bodies.Where(c => c.GroupId == AsteroidDefinitions.BeltGroup).MinBy(c => c.Id)
                        : null;
                var anchor = belt ?? bodies.MinBy(c => c.Id);
                if (anchor is null)
                {
                    continue;
                }
                int slot = definition.Slot;
                double offset = belt is null
                    ? CombatAnomalyRules.PlacementOffset * (IncursionPlacementSlot + slot - FirstIncursionSlot)
                    : 20_000;
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
                contacts.Add(Contact(site, definition.Encounter, definition));
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

    private static CombatAnomalyDefinition SiteDefinition(CatalogDbContext catalog, WorldCombatAnomaly site)
    {
        var definition = site.IncursionTaleId.HasValue
            ? IncursionDefinition(catalog, site)?.Encounter
            : CombatAnomalyCatalog.Find(catalog, site.DungeonId);
        return definition ?? throw new InvalidDataException($"Anomaly definition {site.DungeonId} is missing.");
    }

    private static IncursionSiteDefinition? IncursionDefinition(CatalogDbContext catalog, WorldCombatAnomaly site) =>
        IncursionSiteCatalog.Read(catalog, site.DungeonId);

    private static IncursionCargoObjective? PendingCargo(CatalogDbContext catalog, WorldCombatAnomaly site) =>
        site.IncursionTaleId.HasValue && !site.IncursionCargoDelivered ? IncursionDefinition(catalog, site)?.Cargo : null;

    internal static void DeliverIncursionCargo(
        WorldAction world,
        CatalogDbContext catalog,
        WorldCombatAnomaly site,
        IncursionCargoObjective objective,
        long now
    )
    {
        site.IncursionCargoDelivered = true;
        if (objective.UnlockWave.HasValue)
        {
            NextWave(world, catalog, site, now);
        }
        else
        {
            Complete(site, now);
            FinishIncursionSite(world, catalog, site, now);
        }
        Invalidate(world, site.SystemId);
    }

    private static void FinishIncursionSite(WorldAction world, CatalogDbContext catalog, WorldCombatAnomaly site, long now)
    {
        RetireIncursionOre(world, site);
        RetireIncursionArray(world, site);
        IncursionStore.CompleteSite(world, catalog, site, now);
    }

    private static void StartIncursionOre(WorldAction world, CatalogDbContext catalog, WorldCombatAnomaly site)
    {
        if (site.IncursionTaleId is null || site.DungeonId != IncursionSiteCatalog.NationMiningColony)
        {
            return;
        }
        var position = IncursionSiteCatalog.CombatCenter(site) + new SpacePosition(-33_000, 26_000, -29_000);
        var type = catalog.RequireItem(IncursionSiteCatalog.Lyavite);
        world.Add(
            new WorldAsteroid
            {
                Id = IncursionObjectIds.Object(site.Id, IncursionObjectIds.OreDeposit),
                SystemId = site.SystemId,
                TypeId = type.Id,
                Quantity = 1200,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                Radius = checked((float)(type.Radius ?? 400)),
            }
        );
    }

    private static void RetireIncursionOre(WorldAction world, WorldCombatAnomaly site)
    {
        if (
            site.DungeonId == IncursionSiteCatalog.NationMiningColony
            && world.Set<WorldAsteroid>().Find(IncursionObjectIds.Object(site.Id, IncursionObjectIds.OreDeposit)) is { } ore
        )
        {
            ore.Quantity = 0;
        }
    }

    internal static void RequireIncursionOre(WorldAction world, long targetId, long now)
    {
        if (
            IncursionObjectIds.IsOre(targetId)
            && (
                world.Set<WorldCombatAnomaly>().Find(IncursionObjectIds.Site(targetId))
                    is not { CompletedAt: null, IncursionTaleId: long tale }
                || now < IncursionRules.OpensAt(tale)
                || now >= IncursionRules.EndsAt(tale)
            )
        )
        {
            throw new InvalidDataException("The Incursion ore deposit is no longer available.");
        }
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
            RetireIncursionOre(world, site);
            RetireIncursionArray(world, site);
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
            || site.CompletedAt.HasValue
            || ship.Position is not { } position
            || (position.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId) != site.SystemId
            || IncursionStore.ActiveFocus(world, catalog, site.SystemId, now)?.TaleId != site.IncursionTaleId
        )
        {
            throw new InvalidDataException("The Incursion acceleration gate is unavailable in your current room.");
        }
        var definition = IncursionDefinition(catalog, site);
        var source = definition?.Gates.FirstOrDefault(gate => gateId == DedGateId(site.Id, gate.Slot));
        if (source is null)
        {
            throw new InvalidDataException("The Incursion site does not have this acceleration gate.");
        }
        var origin = DedOrigin(site);
        MissionGate gate = new(
            gateId,
            origin + source.Body.Position,
            source.Body.Radius,
            origin + source.Destination,
            source.EarliestWave > -1
        )
        {
            Access = source.Access,
            DungeonObjectId = source.Body.Id,
        };
        var type = catalog.RequireItem(ship.TypeId);
        if (!MissionGateAccessRules.AllowsShip(gate.Access, type.Id, type.GroupId, type.CategoryId, 0))
        {
            throw new InvalidDataException("This ship is not permitted through this acceleration gate.");
        }
        MissionGateRules.Validate(
            new(position.X, position.Y, position.Z),
            type.Radius ?? 0,
            type.GroupId,
            gate,
            site.Wave >= source.EarliestWave
        );
        return (site, pilot, gate);
    }
}
