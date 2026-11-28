using Eden.Data.Catalog;
using Eden.Data.World.Alliances;
using Eden.Data.World.Characters;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Maps;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Docking;
using Eden.World.Flight;
using Eden.World.Maps;
using Eden.World.Propulsion;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;
using Eden.World.Storage;

namespace Eden.World.Structures;

[Injectable]
internal sealed class PilotJumpBridgeStore(WorldDatabase database, SqliteWorld ships, TimeProvider clock) : IPilotJumpBridges
{
    private sealed record Bridge(SavedStructure Structure, SavedJumpBridge State);

    private sealed record Profile(double Capacity, double Recharge, double Range, InventoryTypeList Excluded, double[] ZoneModifiers);

    private sealed record TransferPlan(Pilot Pilot, Bridge Source, Bridge Exit, double Remaining, double ShipRadius);

    private static readonly CatalogDefinition<Profile> s_profile = new("ansiblex", LoadProfile);
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
    private LiveWorld Live =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Jump bridges require the resident world.");

    public JumpBridgeNetwork Network(long pilotId)
    {
        if (LiveWorld.Find(database.Path) is not { Economy: var economy })
        {
            return new([], [], []);
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        return economy.Read(
            world =>
            {
                var structures = world.Filter<SavedStructure>(row => row.TypeId == JumpBridgeRules.Ansiblex).ToArray();
                foreach (int system in structures.Select(row => row.SystemId).Distinct())
                {
                    CapitalNavigationStore.SettleFuel(world, catalog, system, now);
                }
                List<JumpBridgeLink> links = [];
                List<long> allowed = [];
                List<long> denied = [];
                foreach (var row in structures.OrderBy(row => row.Id))
                {
                    if (
                        Read(world, row.Id) is not { } source
                        || Peer(world, source) is not { } exit
                        || row.Id >= exit.Structure.Id
                        || !Online(world, source, now)
                        || !Online(world, exit, now)
                    )
                    {
                        continue;
                    }
                    links.Add(new(Endpoint(source), Endpoint(exit)));
                    (Access(world, catalog, source, pilotId) ? allowed : denied).Add(row.Id);
                    (Access(world, catalog, exit, pilotId) ? allowed : denied).Add(exit.Structure.Id);
                }
                return new JumpBridgeNetwork(links, allowed, denied);
            },
            new(pilots: [pilotId])
        );
    }

    public IReadOnlyList<JumpBridgeEndpoint> Nearby(int systemId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var profile = catalog.Definition(s_profile, JumpBridgeRules.Ansiblex);
        return Live.Economy.Inspect(world =>
            world
                .Filter<SavedStructure>(row => row.TypeId == JumpBridgeRules.Ansiblex)
                .AsEnumerable()
                .Where(row => row.SystemId == systemId || Distance(catalog, systemId, row.SystemId) <= profile.Range)
                .Select(row => Read(world, row.Id))
                .OfType<Bridge>()
                .OrderBy(row => row.Structure.Id)
                .Select(Endpoint)
                .ToArray()
        );
    }

    public int? Destination(long structureId) => Live.Economy.Inspect(world => Read(world, structureId)?.State.DestinationSystemId);

    public JumpBridgeEndpoint? Linked(long structureId) =>
        Live.Economy.Inspect(world => Read(world, structureId) is { } source && Peer(world, source) is { } exit ? Endpoint(exit) : null);

    public JumpBridgeInfo? Info(long pilotId, long shipId, long structureId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        return Live.Economy.Read(
            world =>
            {
                if (
                    Read(world, structureId) is not { } source
                    || world.Pilots.Find(pilotId) is not { } pilot
                    || pilot.ActiveShipId != shipId
                    || pilot.ActiveShip.Position?.SystemId != source.Structure.SystemId
                    || !Access(world, catalog, source, pilotId)
                )
                {
                    return null;
                }
                CapitalNavigationStore.SettleFuel(world, catalog, source.Structure.SystemId, now);
                if (!Online(world, source, now))
                {
                    return null;
                }
                var profile = catalog.Definition(s_profile, source.Structure.TypeId);
                var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
                return new JumpBridgeInfo(Eligible(profile, hull), Charge(source, profile, now), now);
            },
            new(pilots: [pilotId], ships: [shipId])
        );
    }

    public JumpBridgeDeparture CheckJump(long pilotId, long shipId, long sourceId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var economy = Live.Economy;
        var participants = economy.Inspect(world => Participants(world, pilotId, shipId, sourceId));
        var plan = economy.Inspect(world => Plan(world, catalog, pilotId, shipId, sourceId, null, Now), participants);
        var destination = SystemObjects.FindSystem(catalog, plan.Exit.Structure.SystemId);
        var profile = StructureCatalog.Require(catalog, plan.Exit.Structure.TypeId);
        var exit = plan.Exit.Structure;
        return new(
            new(exit.Id, exit.TypeId, profile.GroupId, profile.CategoryId, exit.CorporationId, Position(exit), (float)profile.Radius),
            destination
        );
    }

    public DockedPilot Transfer(long pilotId, long shipId, long sourceId, long exitId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var economy = Live.Economy;
        var participants = economy.Inspect(world => Participants(world, pilotId, shipId, sourceId));
        using var world = economy.Begin(participants);
        long now = Now;
        var plan = Plan(world, catalog, pilotId, shipId, sourceId, exitId, now);
        var exit = plan.Exit.Structure;
        double radius = StructureCatalog.Require(catalog, exit.TypeId).Radius;
        var point = GateTravelRules.Arrival(shipId, Position(exit), radius, plan.ShipRadius);
        plan.Source.State.Capacitor = plan.Remaining;
        plan.Source.State.ChargedAt = now;
        SqliteWorld.SetPosition(world, catalog, shipId, point, exit.SystemId, now);
        GateCloakPersistence.Start(world, shipId, now);
        MapStatisticsStore.Record(world, exit.SystemId, now, MapActivityKind.Jump);
        var moved = ships.FindPilot(world, plan.Pilot.AccountId, pilotId)!;
        world.Commit();
        return moved;
    }

    internal static void Place(WorldAction world, CatalogDbContext catalog, SavedStructure structure, int? destination, long now)
    {
        if (structure.TypeId != JumpBridgeRules.Ansiblex)
        {
            if (destination.HasValue)
            {
                throw new InvalidDataException("Only an Ansiblex can be aligned to another solar system.");
            }
            return;
        }
        var profile = catalog.Definition(s_profile, structure.TypeId);
        double? distance = destination is int target ? Distance(catalog, structure.SystemId, target) : null;
        if (
            destination is not int system
            || distance is not double ly
            || ly <= 0
            || ly > profile.Range
            || catalog.Read(CatalogData.Client.Systems, system)?.WormholeClassID != 9
        )
        {
            throw new InvalidDataException("Choose a different null-security solar system within the Ansiblex's range.");
        }
        if (
            SystemObjects
                .Geometry(catalog, structure.SystemId)
                .Any(body =>
                    body.GroupId == JumpBridgeRules.StargateGroup
                    && Position(structure).DistanceTo(body.Position) < JumpBridgeRules.MinimumStargateSeparation
                )
        )
        {
            throw new InvalidDataException("The Ansiblex must be at least 100,000 km from every stargate.");
        }
        world.Add(
            new SavedJumpBridge
            {
                StructureId = structure.Id,
                DestinationSystemId = system,
                Capacitor = profile.Capacity,
                ChargedAt = now,
            }
        );
    }

    internal static JumpBridgeContact? Contact(WorldAction world, SavedStructure structure, long now) =>
        structure.TypeId == JumpBridgeRules.Ansiblex && Read(world, structure.Id) is { } bridge
            ? new(bridge.State.DestinationSystemId, Online(world, bridge, now), bridge.State.ChargedAt)
            : null;

    internal static bool CanUse(WorldAction world, CatalogDbContext catalog, SavedStructure structure, long pilot, long now) =>
        Read(world, structure.Id) is { } source
        && Online(world, source, now)
        && Peer(world, source) is { } exit
        && Online(world, exit, now)
        && Access(world, catalog, source, pilot);

    private static TransferPlan Plan(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        long sourceId,
        long? exitId,
        long now
    )
    {
        if (
            world is not EconomyTransaction
            || world.Pilots.Find(pilotId) is not { } pilot
            || pilot.ActiveShipId != shipId
            || pilot.ActiveShip.Position is not { SystemId: int system } position
            || Read(world, sourceId) is not { } source
            || source.Structure.SystemId != system
            || Peer(world, source) is not { } exit
            || exitId.HasValue && exit.Structure.Id != exitId
        )
        {
            throw new InvalidDataException("Use your active ship at the linked Ansiblex.");
        }
        CapitalNavigationStore.SettleFuel(world, catalog, system, now);
        CapitalNavigationStore.SettleFuel(world, catalog, exit.Structure.SystemId, now);
        if (!Online(world, source, now) || !Online(world, exit, now) || !Access(world, catalog, source, pilotId))
        {
            throw new InvalidDataException("The Ansiblex link is offline or access is denied.");
        }
        var profile = catalog.Definition(s_profile, source.Structure.TypeId);
        var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
        if (!Eligible(profile, hull))
        {
            throw new InvalidDataException("This ship type cannot use the Ansiblex jump bridge.");
        }
        var point = new SpacePosition(position.X, position.Y, position.Z);
        double radius = hull.Radius ?? 0;
        double gateRadius = StructureCatalog.Require(catalog, source.Structure.TypeId).Radius;
        if (point.DistanceTo(Position(source.Structure)) > radius + gateRadius + GateTravelRules.ActivationRange)
        {
            throw new InvalidDataException("Approach within the Ansiblex activation range.");
        }
        world.RequireUncloaked(shipId);
        PilotCapitalTravelStore.RequireMobile(world, catalog, pilotId, shipId, now);
        Eden.World.Combat.LanceDisruption.RequireTravel(world, shipId, now);
        EntosisPersistence.RequireTravel(world, shipId, now);
        CrimewatchPersistence.CheckTravel(world, catalog, pilot, CrimewatchTravel.Jump, now, exit.Structure.SystemId);
        if (
            world
                .Filter<WorldTackle>(row =>
                    row.TargetId == shipId
                    && row.TargetKind == TackleTargetKind.Ship
                    && row.SystemId == system
                    && row.ExpiresAt > now
                    && row.Strength > 0
                )
                .Any()
            || NpcEwarStore.Active(world, shipId).Any(effect => effect.ReadyAt > now && effect.Applied.WarpStrength > 0)
            || PilotInterdictionStore.BlocksJump(world, system, point, now)
        )
        {
            throw new InvalidDataException("Warp disruption prevents this Ansiblex jump.");
        }
        var claim = world.Set<SavedSovereigntyClaim>().Find(system)!;
        int? capital = SovereigntyDevelopmentPersistence.CapitalAt(world, claim.AllianceId, now);
        int zone = JumpBridgeRules.Zone(capital is int owned ? Distance(catalog, owned, exit.Structure.SystemId) : null);
        double cost =
            catalog.FittingAttributes(hull.Id).GetValueOrDefault(DogmaAttributeId.BaseJumpBridgeCapacitorCost)
            * profile.ZoneModifiers[zone];
        double remaining = Charge(source, profile, now) - cost;
        if (remaining < 0)
        {
            throw new InvalidDataException("The Ansiblex does not have enough capacitor charge for this jump.");
        }
        return new(pilot, source, exit, remaining, radius);
    }

    private static TransactionParticipants Participants(WorldAction world, long pilotId, long shipId, long sourceId) =>
        Read(world, sourceId) is { } source
            ? new(systems: [source.Structure.SystemId, source.State.DestinationSystemId], pilots: [pilotId], ships: [shipId])
            : new(pilots: [pilotId], ships: [shipId]);

    private static Bridge? Read(WorldAction world, long id) =>
        world.Set<SavedStructure>().Find(id)
            is { TypeId: JumpBridgeRules.Ansiblex, State: not (StructureState.Destroyed or StructureState.Unanchored) } row
        && world.Set<SavedJumpBridge>().Find(id) is { } state
            ? new(row, state)
            : null;

    private static Bridge? Peer(WorldAction world, Bridge source) =>
        world
            .Filter<SavedStructure>(row => row.SystemId == source.State.DestinationSystemId && row.TypeId == JumpBridgeRules.Ansiblex)
            .AsEnumerable()
            .Select(row => Read(world, row.Id))
            .OfType<Bridge>()
            .FirstOrDefault(bridge => bridge.State.DestinationSystemId == source.Structure.SystemId);

    private static bool Online(WorldAction world, Bridge bridge, long now) =>
        CapitalNavigationStore.OnlineService(world, bridge.Structure, JumpBridgeRules.Service, now) is not null;

    private static bool Access(WorldAction world, CatalogDbContext catalog, Bridge source, long pilotId) =>
        world.Pilots.Find(pilotId) is { } pilot
        && world.Set<SavedAllianceMember>().Find(pilot.CorporationId) is { } membership
        && world.Set<SavedSovereigntyClaim>().Find(source.Structure.SystemId)?.AllianceId == membership.AllianceId
        && PilotStructureStore.ServiceAccess(world, catalog, source.Structure, pilotId);

    private static JumpBridgeEndpoint Endpoint(Bridge bridge) =>
        new(
            bridge.Structure.Id,
            bridge.Structure.SystemId,
            bridge.State.DestinationSystemId,
            bridge.Structure.CorporationId,
            bridge.Structure.Name
        );

    private static SpacePosition Position(SavedStructure row) => new(row.X, row.Y, row.Z);

    private static bool Eligible(Profile profile, CatalogItemDefinition hull) =>
        !profile.Excluded.Contains(hull.Id, hull.GroupId, hull.CategoryId);

    private static double Charge(Bridge bridge, Profile profile, long now) =>
        ChargedResource.Recharged(
            bridge.State.Capacitor,
            profile.Capacity,
            profile.Recharge,
            (now - bridge.State.ChargedAt) / (double)TimeSpan.TicksPerMillisecond
        );

    private static double? Distance(CatalogDbContext catalog, int first, int second)
    {
        SpacePosition? Center(int system) =>
            catalog.Read(CatalogData.Client.Systems, system)?.Center is { X: double x, Y: double y, Z: double z } ? new(x, y, z) : null;
        return Center(first) is { } origin && Center(second) is { } target
            ? origin.DistanceTo(target) / CapitalTravelRules.LightYearMeters
            : null;
    }

    private static Profile LoadProfile(CatalogDbContext catalog, int typeId)
    {
        var attributes = catalog.FittingAttributes(typeId);
        var configuration = catalog.Require(CatalogData.Client.Spacecomponentsbytype, (long)typeId)["structureJumpBridge"];
        double Read(string name) =>
            configuration[name] switch
            {
                ClientSpacecomponentsbytypeValueValueChoiceLong value => value.Value,
                ClientSpacecomponentsbytypeValueValueChoiceDouble value => value.Value,
                _ => throw new InvalidDataException("Invalid jump bridge catalog data."),
            };
        return new(
            attributes[DogmaAttributeId.CapacitorCapacity],
            attributes[DogmaAttributeId.CapacitorRechargeTime],
            attributes[DogmaAttributeId.JumpDriveRange],
            CatalogTypeLists.Read(catalog, (long)Read("restrictionTypeList")),
            [Read("zone1Modifier"), Read("zone2Modifier"), Read("zone3Modifier"), Read("zone4Modifier"), Read("zone5Modifier")]
        );
    }
}
