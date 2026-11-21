using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Docking;
using Eden.Data.World.Fittings;
using Eden.Data.World.Flight;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
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
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Maps;
using Eden.World.Propulsion;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Sovereignty;
using Eden.World.Storage;
using Eden.World.Structures;
using Eden.World.Thermal;

namespace Eden.World.Flight;

[Injectable]
internal sealed partial class PilotCapitalTravelStore(
    WorldDatabase database,
    SqliteWorld ships,
    ICooperativeFleets fleets,
    TimeProvider clock,
    ICapitalInventoryEvents inventoryEvents
) : IPilotCapitalTravel, IPilotShipCompression
{
    private sealed record JumpPlan(
        Pilot Pilot,
        JumpDestination Destination,
        IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
        OwnedItem[] Fuel,
        int Quantity,
        double CapacitorCost,
        JumpTimers Timers,
        long? FuelPayer = null
    );

    private sealed record JumpDestination(int FieldType, SpacePosition Position, double SpawnRadius, double StructureRadius = 0);

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
    private LiveWorld Live =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Capital travel requires the resident world.");

    public CynoActivation Light(long pilotId, long shipId, long moduleId, double speed)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = ActivePilot(action, pilotId, shipId);
        var position = pilot.ActiveShip.Position!;
        int system = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        long now = Now;
        var field = action.Set<SavedCynosuralField>().Find(pilotId);
        if (Active(action, field, now))
        {
            throw new InvalidDataException("A cynosural field is already active on this ship.");
        }
        RequireCynoSystem(catalog, system);
        RequireReady(action, pilot, now);
        var item = action.Set<OwnedItem>().Find(moduleId);
        if (
            item is not { Online: true, Singleton: true }
            || CapitalTravelRules.ModuleField(item.TypeId) is not int fieldType
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || FittingCatalog.DefaultEffect(catalog, item.TypeId) != DogmaEffect.CynosuralGeneration
            || !InventoryDefinitions.CanFitShip(
                catalog.FittingAttributes(item.TypeId),
                pilot.ActiveShip.TypeId,
                catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId
            )
        )
        {
            throw new InvalidDataException("Fit and online a supported cynosural field generator on your active ship.");
        }
        if (!CapitalTravelRules.Covert(fieldType))
        {
            RequireUnjammed(action, catalog, system, now);
            if (Inhibited(action, catalog, system, new(position.X, position.Y, position.Z), now))
            {
                throw new InvalidDataException("A mobile inhibitor blocks cynosural ignition at this position.");
            }
        }
        PilotSkillReader.CheckRequirements(action, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(action, catalog, moduleId, now);
        var fit = FittingCalculation.Read(action, catalog, pilot);
        var module = fit.Graph.Attributes(moduleId);
        double maxSpeed = module.GetValueOrDefault(DogmaAttributeId.MaxVelocityActivationLimit);
        if (!double.IsFinite(speed) || speed < 0 || maxSpeed <= 0 || speed >= maxSpeed)
        {
            throw new InvalidDataException("Slow down before activating the cynosural field generator.");
        }
        var ship = PilotFittingStore.ShipAttributes(action, catalog, pilot, calculation: fit);
        RequireLiving(action, pilot, ship, now);
        int ozone = checked((int)Math.Round(module[DogmaAttributeId.ConsumptionQuantity], 8));
        var fuel = Fuel(action, pilotId, shipId, InventoryFlag.Cargo, checked((int)module[DogmaAttributeId.ConsumptionType]), ozone);
        var body = catalog.FittingType(fieldType) ?? throw new InvalidDataException("Missing cynosural field type.");
        long ends = SpatialModuleRules.Deadline(now, module[DogmaAttributeId.Duration]);
        if (action.Set<ActiveFittedModule>().Find(moduleId) is { } oldCycle)
        {
            oldCycle.ExpiresAt = ends;
        }
        else
        {
            action.Add(
                new ActiveFittedModule
                {
                    ShipId = shipId,
                    ModuleId = moduleId,
                    ExpiresAt = ends,
                }
            );
        }
        field ??= action.Add(new SavedCynosuralField { PilotId = pilotId }).Entity;
        field.ShipId = shipId;
        field.ModuleId = moduleId;
        field.BeaconId = action.NextItemId();
        field.FleetId = fleets.Identity(pilotId)?.FleetId ?? 0;
        field.SystemId = system;
        field.TypeId = fieldType;
        field.StartedAt = now;
        field.EndsAt = ends;
        (field.X, field.Y, field.Z) = (position.X, position.Y, position.Z);
        field.Radius = (float)(body.Radius ?? 0);
        field.SpawnRadius = module[DogmaAttributeId.CynosuralFieldSpawnRadius];
        double capacitor = ShipResources.SpendCapacitor(
            action,
            shipId,
            ship,
            module.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            now
        );
        var changes = SpendFuel(action, catalog, fuel, ozone);
        var result = new CynoActivation(Contact(field), changes, capacitor);
        action.Commit();
        return result;
    }

    public void CheckJump(long pilotId, long shipId, CapitalJumpRequest request)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = Live.Economy.Inspect(world => Plan(world, catalog, pilotId, shipId, request, Now), Participants(pilotId, shipId, request));
    }

    public CapitalJump Jump(long pilotId, long shipId, CapitalJumpRequest request)
    {
        var live = Live;
        using var action = live.Economy.Begin(Participants(pilotId, shipId, request));
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        var plan = Plan(action, catalog, pilotId, shipId, request, now);
        var before = PilotFleetStore.Read(catalog, plan.Pilot.ActiveShip);
        var arrival = Arrival(catalog, request.SystemId, plan.Destination, catalog.RequireItem(plan.Pilot.ActiveShip.TypeId).Radius ?? 0);
        double capacitor =
            plan.CapacitorCost > 0
                ? ShipResources.SpendCapacitor(action, shipId, plan.Attributes, plan.CapacitorCost, now)
                : ShipResources.Capacitor(action, shipId, plan.Attributes, now);
        var changes = SpendFuel(action, catalog, plan.Fuel, plan.Quantity);
        List<InventoryChange> passengerChanges = plan.FuelPayer.HasValue ? [] : changes;
        SqliteWorld.SetPosition(action, catalog, shipId, arrival, request.SystemId, now);
        MapStatisticsStore.Record(action, request.SystemId, now, MapActivityKind.Jump);
        var timers = action.Set<SavedJumpTimers>().Find(pilotId) ?? action.Add(new SavedJumpTimers { PilotId = pilotId }).Entity;
        (timers.ActivationUntil, timers.FatigueUntil, timers.UpdatedAt) = (
            plan.Timers.ActivationUntil,
            plan.Timers.FatigueUntil,
            plan.Timers.UpdatedAt
        );
        passengerChanges.Add(new(PilotFleetStore.Read(catalog, plan.Pilot.ActiveShip), before));
        var result = new CapitalJump(ships.FindPilot(action, plan.Pilot.AccountId, pilotId)!, plan.Timers, capacitor, passengerChanges);
        action.Commit();
        if (plan.FuelPayer is long payer)
        {
            inventoryEvents.Publish(payer, changes);
        }
        live.Heat.ClearSelection(pilotId, shipId);
        return result;
    }

    public JumpTimers? Timers(long pilotId) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
                world.Set<SavedJumpTimers>().Find(pilotId) is { } row
                    ? new JumpTimers(row.ActivationUntil, row.FatigueUntil, row.UpdatedAt)
                    : null
            );

    public CynosuralField? Field(long pilotId)
    {
        long now = Now;
        return LiveWorld.Find(database.Path) is { Economy: var economy } && economy.MayHaveCynos(now)
            ? economy.Inspect(
                world =>
                {
                    var row = world.Set<SavedCynosuralField>().Find(pilotId);
                    return Active(world, row, now) ? Contact(row!) : null;
                },
                new(pilots: [pilotId])
            )
            : null;
    }

    public IReadOnlyList<CynosuralField> Fields(int systemId)
    {
        long now = Now;
        return LiveWorld.Find(database.Path) is { Economy: var economy } && economy.MayHaveCynos(now)
            ? economy.Inspect(
                world =>
                    ProjectFields(world, world.Filter<SavedCynosuralField>(row => row.SystemId == systemId), now)
                        .Concat(
                            MobileRows(world, systemId, now)
                                .AsEnumerable()
                                .Where(row => MobileBeaconActive(row, now))
                                .Select(MobileBeaconContact)
                        )
                        .ToArray(),
                new(systems: [systemId])
            )
            : [];
    }

    public IReadOnlyList<CynosuralField> FleetFields(long pilotId)
    {
        long now = Now;
        if (
            LiveWorld.Find(database.Path) is not { Economy: var economy }
            || !economy.MayHaveCynos(now)
            || fleets.Identity(pilotId) is not { FleetId: var fleet }
        )
        {
            return [];
        }
        return economy.Inspect(world =>
            ProjectFields(world, world.Filter<SavedCynosuralField>(row => row.FleetId == fleet), now)
                .Where(field => fleets.Identity(field.PilotId)?.FleetId == fleet)
                .Concat(
                    world
                        .Filter<SavedCynoDeployable>(row => row.FleetId == fleet)
                        .AsEnumerable()
                        .Where(row => MobileBeaconActive(row, now))
                        .Select(MobileBeaconContact)
                )
                .ToArray()
        );
    }

    public IReadOnlyDictionary<int, int> BeaconCounts() =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
            {
                long now = Now;
                return world
                    .Set<SavedCynosuralField>()
                    .Where(row => row.TypeId != CapitalTravelRules.CovertField && Active(world, row, now))
                    .Select(row => row.SystemId)
                    .AsEnumerable()
                    .Concat(
                        world
                            .Set<SavedCynoDeployable>()
                            .AsEnumerable()
                            .Where(row => row.TypeId != CapitalTravelRules.CovertMobileBeacon && MobileBeaconActive(row, now))
                            .Select(row => row.SystemId)
                    )
                    .GroupBy(system => system)
                    .ToDictionary(group => group.Key, group => group.Count());
            })
        ?? new Dictionary<int, int>();

    internal static void RequireMobile(WorldAction world, long pilotId, long shipId, long now)
    {
        if (ActiveMode(world, shipId, now) is not null)
        {
            throw new InvalidDataException("Wait for the active capital mode to finish before moving the ship.");
        }
        var field = world.Set<SavedCynosuralField>().Find(pilotId);
        if (field?.ShipId == shipId && Active(world, field, now))
        {
            throw new InvalidDataException("Wait for the active cynosural field to expire before moving the ship.");
        }
        if (world.Set<SavedJumpPortal>().Find(pilotId) is { } portal && portal.ShipId == shipId && PortalActive(world, portal, now))
        {
            throw new InvalidDataException("Wait for the active jump portal to close before moving the ship.");
        }
    }

    private JumpPlan Plan(WorldAction world, CatalogDbContext catalog, long pilotId, long shipId, CapitalJumpRequest request, long now)
    {
        var pilot = ActivePilot(world, pilotId, shipId);
        if (request.Target == CapitalJumpTarget.FleetPortal)
        {
            return PlanPortal(world, catalog, pilot, request, now);
        }
        RequireCynoSystem(catalog, request.SystemId);
        if (request.Target == CapitalJumpTarget.Structure)
        {
            CapitalNavigationStore.SettleFuel(world, catalog, request.SystemId, now);
        }
        var destination = Destination(world, catalog, pilotId, request, now);
        if (!CapitalTravelRules.Covert(destination.FieldType))
        {
            RequireUnjammed(world, catalog, request.SystemId, now);
        }
        var position = pilot.ActiveShip.Position!;
        int origin = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        if (origin == request.SystemId || !CapitalTravelRules.JumpDrive(catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId))
        {
            throw new InvalidDataException("Use an eligible jump drive ship in a different system to jump to a cyno.");
        }
        var previous = RequireJumpReady(world, catalog, pilot, origin, request.SystemId, now);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, pilot.ActiveShip.TypeId);
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        RequireLiving(world, pilot, ship, now);
        double distance = DriveDistance(catalog, origin, request.SystemId, destination, ship);
        int isotopes = CapitalTravelRules.Isotopes(distance, ship[DogmaAttributeId.JumpDriveConsumptionAmount]);
        int type = checked((int)ship[DogmaAttributeId.JumpDriveConsumptionType]);
        var fuel = Fuel(world, pilotId, shipId, InventoryFlag.FuelBay, type, isotopes);
        double cost = ship[DogmaAttributeId.JumpDriveCapacitorNeed] * ship[DogmaAttributeId.CapacitorCapacity];
        if (!double.IsFinite(cost) || cost <= 0 || ShipResources.Capacitor(world, shipId, ship, now) < cost)
        {
            throw new InvalidDataException("Insufficient capacitor to jump.");
        }
        var timers = CapitalTravelRules.AfterJump(
            now,
            previous?.FatigueUntil ?? 0,
            distance,
            ship.GetValueOrDefault(DogmaAttributeId.JumpFatigueMultiplier, 1)
        );
        return new(pilot, destination, ship, fuel, isotopes, cost, timers);
    }

    private static SavedJumpTimers? RequireJumpReady(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        int origin,
        int system,
        long now
    )
    {
        RequireReady(world, pilot, now);
        CrimewatchPersistence.CheckTravel(world, catalog, pilot, CrimewatchTravel.Jump, now, system);
        long shipId = pilot.ActiveShipId;
        var position = pilot.ActiveShip.Position!;
        if (
            world
                .Filter<WorldTackle>(row =>
                    row.TargetId == shipId
                    && row.TargetKind == TackleTargetKind.Ship
                    && row.SystemId == origin
                    && row.ExpiresAt > now
                    && row.Strength > 0
                )
                .Any()
            || NpcEwarStore.Active(world, shipId).Any(effect => effect.ReadyAt > now && effect.Applied.WarpStrength > 0)
            || PilotInterdictionStore.BlocksJump(world, origin, new(position.X, position.Y, position.Z), now)
        )
        {
            throw new InvalidDataException("Warp disruption prevents this jump.");
        }
        var previous = world.Set<SavedJumpTimers>().Find(pilot.Id);
        if (previous?.ActivationUntil > now)
        {
            throw new InvalidDataException("Wait for the jump activation cooldown to finish.");
        }
        return previous;
    }

    private static double DriveDistance(
        CatalogDbContext catalog,
        int origin,
        int target,
        JumpDestination destination,
        IReadOnlyDictionary<DogmaAttributeId, double> ship
    )
    {
        long list = checked((long)ship[DogmaAttributeId.JumpDriveTargetBeaconTypeList]);
        if (catalog.Read(CatalogData.Client.Typelist, list)?.IncludedTypeIDs?.Contains(destination.FieldType) != true)
        {
            throw new InvalidDataException("This ship cannot jump to that type of cynosural field.");
        }
        double distance = Center(catalog, origin).DistanceTo(Center(catalog, target)) / CapitalTravelRules.LightYearMeters;
        if (!double.IsFinite(distance) || distance <= 0 || distance > ship[DogmaAttributeId.JumpDriveRange])
        {
            throw new InvalidDataException("The cynosural field is outside jump range.");
        }
        return distance;
    }

    private JumpDestination Destination(WorldAction world, CatalogDbContext catalog, long pilotId, CapitalJumpRequest request, long now)
    {
        if (request.Target == CapitalJumpTarget.FleetDeployable)
        {
            if (
                MobileTarget(world, request.EmitterId, now) is not { } deployable
                || deployable.SystemId != request.SystemId
                || deployable.BeaconId != request.BeaconId
                || deployable.FleetId != fleets.Identity(pilotId)?.FleetId
                || !MobileBeaconActive(deployable, now)
            )
            {
                throw new InvalidDataException("That fleet deployable beacon is unavailable.");
            }
            return new(MobileFieldType(deployable.TypeId)!.Value, MobilePosition(deployable), CapitalTravelRules.MobileArrivalClearance);
        }
        if (request.Target == CapitalJumpTarget.Structure)
        {
            if (
                world.Set<SavedStructure>().Find(request.BeaconId) is not { } structure
                || structure.SystemId != request.SystemId
                || !CapitalNavigationStore.CanUseBeacon(world, catalog, structure, pilotId, now)
            )
            {
                throw new InvalidDataException("That structure beacon is unavailable to your pilot.");
            }
            return new(
                CapitalTravelRules.StandardField,
                new(structure.X, structure.Y, structure.Z),
                CapitalTravelRules.StructureArrivalClearance,
                StructureCatalog.Require(catalog, structure.TypeId).Radius
            );
        }
        var field = world.Set<SavedCynosuralField>().Find(request.EmitterId);
        if (
            request.Target != CapitalJumpTarget.FleetModule
            || field is null
            || field.BeaconId != request.BeaconId
            || field.SystemId != request.SystemId
            || field.FleetId == 0
            || fleets.Identity(pilotId)?.FleetId != field.FleetId
            || fleets.Identity(field.PilotId)?.FleetId != field.FleetId
            || !Active(world, field, now)
        )
        {
            throw new InvalidDataException("That fleet cynosural field is no longer available.");
        }
        return new(field.TypeId, new(field.X, field.Y, field.Z), field.SpawnRadius);
    }

    private static TransactionParticipants Participants(long pilotId, long shipId, CapitalJumpRequest request) =>
        new(
            systems: [request.SystemId],
            pilots: request.Target is CapitalJumpTarget.FleetModule or CapitalJumpTarget.FleetPortal
                ? [pilotId, request.EmitterId]
                : [pilotId],
            ships: request.Target == CapitalJumpTarget.FleetPortal ? [shipId, request.PortalShipId] : [shipId]
        );

    private static Pilot ActivePilot(WorldAction world, long pilotId, long shipId) =>
        world.Pilots.Find(pilotId) is { } pilot
        && pilot.ActiveShipId == shipId
        && pilot.ActiveShip is { Position: not null } ship
        && ship.OwnerId == pilotId
        && ship.StoredInId is null
            ? pilot
            : throw new InvalidDataException("Use your active ship in space.");

    private static void RequireReady(WorldAction world, Pilot pilot, long now)
    {
        RequireMobile(world, pilot.Id, pilot.ActiveShipId, now);
        world.RequireUncloaked(pilot.ActiveShipId);
        EntosisPersistence.RequireTravel(world, pilot.ActiveShipId, now);
        if (
            world.Set<SpatialNavigationState>().Find(pilot.ActiveShipId) is { } state
            && (state.GateCloakUntil > now || state.JumpEndsAt > now)
        )
        {
            throw new InvalidDataException("Wait until the ship can activate its jump equipment.");
        }
    }

    private static void RequireLiving(WorldAction world, Pilot pilot, IReadOnlyDictionary<DogmaAttributeId, double> attributes, long now)
    {
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate jump equipment.");
        }
    }

    private static void RequireCynoSystem(CatalogDbContext catalog, int system)
    {
        var data = catalog.Read(CatalogData.Client.Systems, system);
        // Wormhole classes 7–9 locate normal high, low and null security systems in the pinned client.
        if (
            data is not { SecurityStatus: < StructureRules.HighSecurityBoundary, WormholeClassID: 8 or 9 }
            || data.RegionID is 10000004 or 10000017 or 10000019 or 10000070
        )
        {
            throw new InvalidDataException("Cynos require eligible low-security or null-security space.");
        }
    }

    private static void RequireUnjammed(WorldAction world, CatalogDbContext catalog, int system, long now)
    {
        CapitalNavigationStore.SettleFuel(world, catalog, system, now);
        if (CapitalNavigationStore.Jammed(world, catalog, system, now))
        {
            throw new InvalidDataException("This solar system is cynosural jammed.");
        }
    }

    private static SpacePosition Center(CatalogDbContext catalog, int system) =>
        catalog.Read(CatalogData.Client.Systems, system)?.Center is { X: double x, Y: double y, Z: double z }
            ? new(x, y, z)
            : throw new InvalidDataException("The solar system has no jump coordinates.");

    private static bool Active(WorldAction world, SavedCynosuralField? field, long now) =>
        field is { } row
        && row.EndsAt > now
        && world.Positions.Find(row.ShipId)?.SystemId == row.SystemId
        && world.Pilots.Find(row.PilotId)?.ActiveShipId == row.ShipId
        && world.Set<OwnedItem>().Find(row.ModuleId) is { Online: true } item
        && item.LocationId == row.ShipId
        && world.Set<ShipLoss>().Find(row.ShipId) is null;

    private static CynosuralField[] ProjectFields(WorldAction world, IQueryable<SavedCynosuralField> rows, long now) =>
        rows.Where(row => row.EndsAt > now).AsEnumerable().Where(row => Active(world, row, now)).Select(Contact).ToArray();

    private static CynosuralField Contact(SavedCynosuralField row) =>
        new(
            row.PilotId,
            row.ShipId,
            row.ModuleId,
            row.FleetId,
            row.SystemId,
            row.StartedAt,
            row.EndsAt,
            new(
                row.BeaconId,
                row.TypeId,
                CapitalTravelRules.Covert(row.TypeId) ? CapitalTravelRules.CovertFieldGroup : CapitalTravelRules.FieldGroup,
                (int)InventoryCategory.Celestial,
                0,
                new(row.X, row.Y, row.Z),
                row.Radius
            )
            {
                Solid = false,
                IsGlobal = !CapitalTravelRules.Covert(row.TypeId),
                Name = row.TypeId switch
                {
                    CapitalTravelRules.CovertField => "Covert Cynosural Field",
                    CapitalTravelRules.IndustrialField => "Industrial Cynosural Field",
                    _ => "Cynosural Field",
                },
            }
        );

    private static SpacePosition Arrival(CatalogDbContext catalog, int system, JumpDestination destination, double shipRadius)
    {
        var center = destination.Position;
        double offset = destination.SpawnRadius + (destination.StructureRadius > 0 ? destination.StructureRadius + shipRadius : 0);
        var geometry = SystemObjects.Geometry(catalog, system);
        SpacePosition[] directions = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(-1, 0, 0), new(0, -1, 0), new(0, 0, -1)];
        foreach (var direction in directions)
        {
            var point = center + direction * offset;
            if (
                geometry.All(body =>
                    !body.Solid
                    || (
                        body.Collision is { } collision
                            ? collision.Unstick(point - body.Position, shipRadius) == point - body.Position
                            : point.DistanceTo(body.Position) >= body.Radius + shipRadius
                    )
                )
            )
            {
                return point;
            }
        }
        throw new InvalidDataException("There is no clear arrival point around this cynosural field.");
    }

    private static OwnedItem[] Fuel(WorldAction world, long pilotId, long shipId, InventoryFlag flag, int type, int required)
    {
        var stacks = FuelStacks(world, pilotId, shipId, flag, type).ToArray();
        RequireFuel(stacks, required);
        return stacks;
    }

    private static IEnumerable<OwnedItem> FuelStacks(WorldAction world, long pilotId, long shipId, InventoryFlag flag, int type)
    {
        int inventoryFlag = (int)flag;
        return world
            .Filter<OwnedItem>(item =>
                item.LocationId == shipId
                && item.OwnerId == pilotId
                && item.FlagId == inventoryFlag
                && item.TypeId == type
                && !item.Singleton
                && item.Quantity > 0
            )
            .OrderBy(item => item.Id);
    }

    private static void RequireFuel(IReadOnlyList<OwnedItem> stacks, int required)
    {
        if (required <= 0 || stacks.Sum(item => (long)item.Quantity) < required)
        {
            throw new InvalidDataException("The ship does not have enough of the required jump fuel.");
        }
    }

    private static List<InventoryChange> SpendFuel(WorldAction world, CatalogDbContext catalog, OwnedItem[] stacks, int required)
    {
        List<InventoryChange> changes = [];
        foreach (var stack in stacks)
        {
            var before = PilotInventoryStore.Read(catalog, stack);
            int payment = Math.Min(required, stack.Quantity);
            stack.Quantity -= payment;
            changes.Add(new(before with { Quantity = stack.Quantity }, before));
            if (stack.Quantity == 0)
            {
                world.Remove(stack);
            }
            required -= payment;
            if (required == 0)
            {
                break;
            }
        }
        return changes;
    }
}
