using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Flight;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Structures;
using Eden.World.Thermal;

namespace Eden.World.Flight;

internal sealed partial class PilotCapitalTravelStore
{
    private const double PortalActivationRange = 2500;

    public JumpPortalActivation OpenPortal(long pilotId, long shipId, CapitalJumpRequest request)
    {
        using var world = Live.Economy.Begin(Participants(pilotId, shipId, request));
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        var host = ActivePilot(world, pilotId, shipId);
        var hull = catalog.RequireItem(host.ActiveShip.TypeId);
        int moduleType = hull.GroupId switch
        {
            (int)HullClass.Titan => CapitalTravelRules.TitanPortalGenerator,
            (int)HullClass.BlackOps => CapitalTravelRules.CovertPortalGenerator,
            _ => 0,
        };
        if (moduleType == 0 || fleets.Identity(pilotId) is not { FleetId: var fleet })
        {
            throw new InvalidDataException("Use an eligible Titan or Black Ops ship in a fleet to open a jump portal.");
        }
        RequireReady(world, host, now);
        RequireCynoSystem(catalog, request.SystemId);
        var destination = PortalDestination(world, catalog, host.Id, request, now);
        var module =
            world
                .Filter<OwnedItem>(item =>
                    item.LocationId == shipId && item.OwnerId == pilotId && item.TypeId == moduleType && item.Online && item.Singleton
                )
                .AsEnumerable()
                .FirstOrDefault(item => InventoryDefinitions.IsHighSlot(item.FlagId))
            ?? throw new InvalidDataException("Fit and online a compatible jump portal generator.");
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, hull.Id);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, module.Id, now);
        var fit = FittingCalculation.Read(world, catalog, host);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, host, calculation: fit);
        RequireLiving(world, host, attributes, now);
        int origin = host.ActiveShip.Position!.SystemId ?? catalog.RequireStation(host.ActiveShip.StationId).SolarSystemId;
        _ = DriveDistance(catalog, origin, request.SystemId, destination, attributes);
        var fitted = fit.Graph.Attributes(module.Id);
        long end = SpatialModuleRules.Deadline(now, fitted[DogmaAttributeId.JumpPortalDuration]);
        double capacitor = ShipResources.SpendCapacitor(world, shipId, attributes, fitted[DogmaAttributeId.CapacitorNeed], now);
        IReadOnlyList<InventoryChange> fuelChanges = [];
        if (moduleType == CapitalTravelRules.TitanPortalGenerator)
        {
            int level = PilotSkillReader.Level(world, catalog, pilotId, CapitalTravelRules.JumpPortalGenerationSkill);
            double reduction =
                catalog.FittingAttributes(CapitalTravelRules.JumpPortalGenerationSkill)[DogmaAttributeId.JumpPortalFuelBonusPercent]
                * level
                / 100;
            double baseQuantity = catalog.FittingAttributes(moduleType)[DogmaAttributeId.ConsumptionQuantity];
            int quantity = checked((int)Math.Round(baseQuantity * (1 + reduction), MidpointRounding.AwayFromZero));
            int fuelType = checked((int)fitted[DogmaAttributeId.ConsumptionType]);
            var charge = FuelStacks(world, host.Id, shipId, InventoryFlag.Cargo, fuelType).ToArray();
            RequireFuel(charge, quantity);
            fuelChanges = SpendFuel(world, catalog, charge, quantity);
        }
        var active =
            world.Set<ActiveFittedModule>().Find(module.Id)
            ?? world.Add(new ActiveFittedModule { ShipId = shipId, ModuleId = module.Id }).Entity;
        active.ExpiresAt = end;
        var portal = world.Set<SavedJumpPortal>().Find(pilotId) ?? world.Add(new SavedJumpPortal { PilotId = pilotId }).Entity;
        portal.ShipId = shipId;
        portal.ModuleId = module.Id;
        portal.FleetId = fleet;
        portal.Origin = origin;
        portal.Destination = request.SystemId;
        portal.Target = request.Target;
        portal.EmitterId = request.EmitterId;
        portal.BeaconId = request.BeaconId;
        portal.StartedAt = now;
        portal.EndsAt = end;
        var result = new JumpPortalActivation(PortalContact(portal, moduleType), capacitor, fuelChanges);
        world.Commit();
        return result;
    }

    public JumpPortal? Portal(long pilotId)
    {
        long now = Now;
        return LiveWorld.Find(database.Path) is { Economy: var economy } && economy.MayHaveCynos(now)
            ? economy.Inspect(
                world =>
                    world.Set<SavedJumpPortal>().Find(pilotId) is { } portal && PortalActive(world, portal, now)
                        ? PortalContact(portal, PortalModuleType(world, portal))
                        : null,
                new(pilots: [pilotId])
            )
            : null;
    }

    public IReadOnlyList<JumpPortal> FleetPortals(long pilotId)
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
            world
                .Filter<SavedJumpPortal>(portal => portal.FleetId == fleet && portal.EndsAt > now)
                .AsEnumerable()
                .Where(portal => PortalActive(world, portal, now) && fleets.Identity(portal.PilotId)?.FleetId == fleet)
                .Select(portal => PortalContact(portal, PortalModuleType(world, portal)))
                .ToArray()
        );
    }

    private JumpPlan PlanPortal(WorldAction world, CatalogDbContext catalog, Pilot passenger, CapitalJumpRequest request, long now)
    {
        long passengerShip = passenger.ActiveShipId;
        var position = passenger.ActiveShip.Position!;
        int origin = position.SystemId ?? catalog.RequireStation(passenger.ActiveShip.StationId).SolarSystemId;
        if (
            request.EmitterId == passenger.Id
            || world.Set<SavedJumpPortal>().Find(request.EmitterId) is not { } row
            || row.ShipId != request.PortalShipId
            || row.Origin != origin
            || row.Destination != request.SystemId
            || row.BeaconId != request.BeaconId
            || fleets.Identity(passenger.Id)?.FleetId != row.FleetId
            || fleets.Identity(row.PilotId)?.FleetId != row.FleetId
            || !PortalActive(world, row, now)
        )
        {
            throw new InvalidDataException("That fleet jump portal is unavailable.");
        }
        var host = ActivePilot(world, row.PilotId, row.ShipId);
        var hostPosition = host.ActiveShip.Position!;
        var hull = catalog.RequireItem(passenger.ActiveShip.TypeId);
        double separation = new SpacePosition(position.X, position.Y, position.Z).DistanceTo(
            new(hostPosition.X, hostPosition.Y, hostPosition.Z)
        );
        if (separation > PortalActivationRange + (hull.Radius ?? 0) + (catalog.RequireItem(host.ActiveShip.TypeId).Radius ?? 0))
        {
            throw new InvalidDataException("Approach within range of the fleet jump portal.");
        }
        int moduleType = PortalModuleType(world, row);
        var target = new CapitalJumpRequest(row.EmitterId, row.BeaconId, row.Destination, row.Target);
        var destination = PortalDestination(world, catalog, host.Id, target, now);
        var previous = RequireJumpReady(world, catalog, passenger, origin, target.SystemId, now);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, passenger);
        RequireLiving(world, passenger, attributes, now);
        var fit = FittingCalculation.Read(world, catalog, host);
        var hostAttributes = PilotFittingStore.ShipAttributes(world, catalog, host, calculation: fit);
        RequireLiving(world, host, hostAttributes, now);
        var module = fit.Graph.Attributes(row.ModuleId);
        var eligibility = (DogmaAttributeId)checked((int)module[DogmaAttributeId.JumpPortalPassengerRequiredAttribute]);
        var requiredAttribute =
            moduleType == CapitalTravelRules.TitanPortalGenerator
                ? DogmaAttributeId.TitanPortalPassenger
                : DogmaAttributeId.BlackOpsPortalPassenger;
        double defaultEligibility = catalog.Read(CatalogData.Client.Dogmaattributes, (long)requiredAttribute)?.DefaultValue ?? 0;
        if (eligibility != requiredAttribute || attributes.GetValueOrDefault(requiredAttribute, defaultEligibility) <= 0)
        {
            throw new InvalidDataException("This ship cannot use that jump portal.");
        }
        double distance = DriveDistance(catalog, origin, target.SystemId, destination, hostAttributes);
        double mass = attributes.GetValueOrDefault(DogmaAttributeId.Mass, hull.Mass ?? 0);
        double rate = hostAttributes[DogmaAttributeId.JumpDriveConsumptionAmount];
        double required = Math.Ceiling(distance * mass * module[DogmaAttributeId.JumpPortalConsumptionMassFactor] * rate);
        if (!double.IsFinite(required) || required is <= 0 or > int.MaxValue)
        {
            throw new InvalidDataException("Invalid jump portal fuel requirement.");
        }
        int fuelType = checked((int)hostAttributes[DogmaAttributeId.JumpDriveConsumptionType]);
        var fuel = FuelStacks(world, host.Id, row.ShipId, InventoryFlag.FuelBay, fuelType)
            .Concat(FuelStacks(world, host.Id, row.ShipId, InventoryFlag.Cargo, fuelType))
            .ToArray();
        int payment = (int)required;
        RequireFuel(fuel, payment);
        var timers = CapitalTravelRules.AfterJump(
            now,
            previous?.FatigueUntil ?? 0,
            distance,
            attributes.GetValueOrDefault(DogmaAttributeId.JumpFatigueMultiplier, 1)
        );
        return new(passenger, destination, attributes, fuel, payment, 0, timers, host.Id);
    }

    private JumpDestination PortalDestination(WorldAction world, CatalogDbContext catalog, long pilot, CapitalJumpRequest request, long now)
    {
        if (request.Target == CapitalJumpTarget.Structure)
        {
            CapitalNavigationStore.SettleFuel(world, catalog, request.SystemId, now);
        }
        var destination = Destination(world, catalog, pilot, request, now);
        if (!CapitalTravelRules.Covert(destination.FieldType))
        {
            RequireUnjammed(world, catalog, request.SystemId, now);
        }
        return destination;
    }

    private static bool PortalActive(WorldAction world, SavedJumpPortal row, long now) =>
        row.EndsAt > now
        && world.Positions.Find(row.ShipId)?.SystemId == row.Origin
        && world.Pilots.Find(row.PilotId)?.ActiveShipId == row.ShipId
        && world.Set<OwnedItem>().Find(row.ModuleId) is { Online: true, Singleton: true } fitted
        && fitted.TypeId is CapitalTravelRules.CovertPortalGenerator or CapitalTravelRules.TitanPortalGenerator
        && InventoryDefinitions.IsHighSlot(fitted.FlagId)
        && fitted.LocationId == row.ShipId
        && world.Set<ShipLoss>().Find(row.ShipId) is null;

    private static int PortalModuleType(WorldAction world, SavedJumpPortal row) => world.Set<OwnedItem>().Find(row.ModuleId)!.TypeId;

    internal static JumpPortal PortalContact(SavedJumpPortal row, int typeId = CapitalTravelRules.CovertPortalGenerator) =>
        new(
            row.PilotId,
            row.ShipId,
            row.ModuleId,
            row.FleetId,
            row.Origin,
            new(row.EmitterId, row.BeaconId, row.Destination, row.Target),
            row.StartedAt,
            row.EndsAt,
            typeId
        );
}
