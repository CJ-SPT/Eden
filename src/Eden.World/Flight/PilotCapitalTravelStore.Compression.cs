using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Structures;
using Eden.World.Thermal;

namespace Eden.World.Flight;

internal sealed partial class PilotCapitalTravelStore
{
    public ShipCompressorActivation Activate(long pilotId, long shipId, long moduleId)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        var pilot = ActivePilot(action, pilotId, shipId);
        action.RequireUncloaked(shipId);
        var core = ActiveMode(action, shipId, now);
        if (core?.Effect != DogmaEffect.CapitalIndustrialCore)
        {
            throw new InvalidDataException("Activate the capital industrial core before starting compression.");
        }
        var item = action.Set<OwnedItem>().Find(moduleId);
        if (
            !IsFittedCompressor(item, shipId, pilotId)
            || pilot.ActiveShip.TypeId != UtilityDroneRules.Rorqual
            || FittingCatalog.DefaultEffect(catalog, item!.TypeId) != DogmaEffect.IndustrialItemCompression
        )
        {
            throw new InvalidDataException("Use an online fitted capital compressor on your active Rorqual.");
        }
        var cooldown = action.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown?.ReadyAt > now)
        {
            throw new InvalidDataException("The compressor's paid cycle is still running.");
        }
        PilotSkillReader.CheckRequirements(action, catalog, pilotId, item!.TypeId);
        NaniteRepairPersistence.RequireAvailable(action, catalog, moduleId, now);
        var ship = PilotFittingStore.ShipAttributes(action, catalog, pilot);
        RequireLiving(action, pilot, ship, now);
        var attributes = catalog.FittingAttributes(item.TypeId);
        if (attributes.GetValueOrDefault(DogmaAttributeId.Duration) != ShipCompressionRules.CycleMilliseconds)
        {
            throw new InvalidDataException("Unknown compressor cycle.");
        }
        var facility = CompressionFacility(action, catalog, pilotId, item.TypeId);
        double capacitor = ShipResources.SpendCapacitor(action, shipId, ship, attributes[DogmaAttributeId.CapacitorNeed], now);
        long end = checked(now + ShipCompressionRules.CycleMilliseconds * TimeSpan.TicksPerMillisecond);
        var active = action.Set<ActiveFittedModule>().Find(moduleId) ?? action.Add(new ActiveFittedModule { ModuleId = moduleId }).Entity;
        active.ShipId = shipId;
        active.ExpiresAt = end;
        cooldown ??= action.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = end;
        action.Commit();
        return new(new(pilotId, shipId, moduleId, now, Math.Min(end, core.EndsAt), facility), capacitor);
    }

    public void Stop(long pilotId, long shipId, long moduleId)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        _ = ActivePilot(action, pilotId, shipId);
        if (
            IsFittedCompressor(action.Set<OwnedItem>().Find(moduleId), shipId, pilotId)
            && action.Set<ActiveFittedModule>().Find(moduleId) is { } active
        )
        {
            action.Remove(active);
        }
        action.Commit();
    }

    public IReadOnlyList<ShipCompressorCycle> Compressors(long shipId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return [];
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(world => Compressors(world, catalog, shipId, Now));
    }

    public StructureCompressionResult Compress(long pilotId, long shipId, long itemId, long facilityShipId)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId, facilityShipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        var user = ActivePilot(action, pilotId, shipId);
        var owner = action.Pilots.SingleOrDefault(pilot => pilot.ActiveShipId == facilityShipId);
        if (
            owner?.ActiveShip.Position is not { } hostPosition
            || CrimewatchPersistence.System(catalog, user.ActiveShip) != CrimewatchPersistence.System(catalog, owner.ActiveShip)
            || user.Id != owner.Id && (fleets.Identity(user.Id) is not { } fleet || fleets.Identity(owner.Id)?.FleetId != fleet.FleetId)
        )
        {
            throw new InvalidDataException("Choose an active compressor on your ship or a nearby fleet member.");
        }
        var item = action.Set<OwnedItem>().Find(itemId);
        if (
            item is not { Singleton: false, Quantity: > 0 }
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || ShipHoldRules.Definition(item.FlagId) is not { } hold
        )
        {
            throw new InvalidDataException("Choose a resource stack in a hold of your own active ship.");
        }
        var position = user.ActiveShip.Position!;
        double edge =
            shipId == facilityShipId
                ? 0
                : new SpacePosition(position.X, position.Y, position.Z).DistanceTo(new(hostPosition.X, hostPosition.Y, hostPosition.Z))
                    - (catalog.RequireItem(user.ActiveShip.TypeId).Radius ?? 0)
                    - (catalog.RequireItem(owner.ActiveShip.TypeId).Radius ?? 0);
        var before = PilotInventoryStore.Read(catalog, item);
        var enabled = Compressors(action, catalog, facilityShipId, now);
        var eligible =
            enabled
                .Select(active => active.Facility)
                .Where(facility => edge <= facility.Range)
                .Select(facility => CatalogTypeLists.Read(catalog, facility.TypeList))
                .FirstOrDefault(types => types.Contains(before.TypeId, before.GroupId, before.CategoryId))
            ?? throw new InvalidDataException("No compatible active compressor is in range.");
        item.TypeId = StructureCompressionCatalog.Compressed(catalog, item.TypeId, eligible);
        var after = PilotInventoryStore.Read(catalog, item);
        if (
            !hold.Allows(before)
            || !hold.Allows(after)
            || hold.RequiredCapacity(after, after.Quantity)
                > hold.RequiredCapacity(before, before.Quantity) + InventoryDefinitions.ResourceTolerance
        )
        {
            throw new InvalidDataException("The compressed resource cannot occupy this hold.");
        }
        action.Commit();
        return new(before.Id, before.TypeId, before.Quantity, after.Id, after.TypeId, after.Quantity, new(after, before));
    }

    private static ShipCompressorCycle[] Compressors(WorldAction world, CatalogDbContext catalog, long shipId, long now)
    {
        if (
            world is not EconomyTransaction action
            || ActiveMode(world, shipId, now) is not { Effect: DogmaEffect.CapitalIndustrialCore } core
        )
        {
            return [];
        }
        return action
            .TimedModules(shipId)
            .Select(id => (Cycle: world.Set<ActiveFittedModule>().Find(id), Item: world.Set<OwnedItem>().Find(id)))
            .Where(pair => pair.Cycle?.ExpiresAt > now && IsFittedCompressor(pair.Item, shipId, core.PilotId))
            .OrderBy(pair => pair.Item!.Id)
            .Select(pair => new ShipCompressorCycle(
                core.PilotId,
                shipId,
                pair.Item!.Id,
                pair.Cycle!.ExpiresAt - ShipCompressionRules.CycleMilliseconds * TimeSpan.TicksPerMillisecond,
                Math.Min(core.EndsAt, pair.Cycle.ExpiresAt),
                CompressionFacility(world, catalog, core.PilotId, pair.Item.TypeId)
            ))
            .ToArray();
    }

    private static bool IsFittedCompressor(OwnedItem? item, long shipId, long pilotId) =>
        item is { Singleton: true, Online: true }
        && item.OwnerId == pilotId
        && item.LocationId == shipId
        && InventoryDefinitions.IsHighSlot(item.FlagId)
        && ShipCompressionRules.IsCompressor(item.TypeId);

    private static ShipCompressionFacility CompressionFacility(WorldAction world, CatalogDbContext catalog, long pilotId, int module)
    {
        var attributes = catalog.FittingAttributes(module);
        int list = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.CompressibleItemsTypeList));
        int skill = PilotSkillReader.OperationalLevel(world, catalog, pilotId, ShipCompressionRules.FleetCompressionLogistics);
        double range = attributes.GetValueOrDefault(DogmaAttributeId.MaxRange) * (1 + skill * 0.1);
        if (list <= 0 || !double.IsFinite(range) || range <= 0)
        {
            throw new InvalidDataException("Unknown compressor range or resource class.");
        }
        return new(list, range);
    }
}
