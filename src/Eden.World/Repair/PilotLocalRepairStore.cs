using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Repair;

[Injectable]
internal sealed class PilotLocalRepairStore(WorldDatabase database) : IPilotLocalRepairing
{
    public LocalRepairActivation Start(
        long pilotId,
        long moduleId,
        long now,
        RepairLayer layer = RepairLayer.Armor,
        bool ancillary = false
    ) => StartCycle(pilotId, moduleId, now, layer, ancillary, false);

    // Native overload activation remains gated on the damage/burnout owner.
    internal LocalRepairActivation StartOverloaded(
        long pilotId,
        long moduleId,
        long now,
        RepairLayer layer = RepairLayer.Armor,
        bool ancillary = false
    ) => StartCycle(pilotId, moduleId, now, layer, ancillary, true);

    private LocalRepairActivation StartCycle(long pilotId, long moduleId, long now, RepairLayer layer, bool ancillary, bool overloaded)
    {
        if (ancillary && layer != RepairLayer.Armor)
        {
            throw new InvalidDataException("Ancillary local repairs apply to armor or shields, not hull.");
        }
        var profile = LocalRepairDefinitions.Profile(layer);
        using WorldActionScope transaction = new(database, LocalParticipants(pilotId, moduleId));
        var world = transaction.World;
        overloaded |= PilotOverloadStore.Selected(world, database.Path, pilotId, moduleId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var module = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot.ActiveShip.Position is null
            || module is null
            || module.OwnerId != pilotId
            || module.LocationId != pilot.ActiveShipId
            || !module.Singleton
            || !module.Online
            || !(
                profile.Rack == ModuleRack.Low
                    ? InventoryDefinitions.IsLowSlot(module.FlagId)
                    : InventoryDefinitions.IsMediumSlot(module.FlagId)
            )
            || catalog.RequireItem(module.TypeId).GroupId != (ancillary ? AncillaryRepairRules.ArmorGroup : profile.GroupId)
            || FittingCatalog.Definition(catalog, module.TypeId) is null
        )
        {
            throw new InvalidDataException("Use an online fitted local repairer of the requested kind on your active ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, module.Id, now);
        var calculation = overloaded
            ? FittingCalculation.Read(world, catalog, pilot, overloadedModules: new HashSet<long> { moduleId })
            : null;
        var attributes = PilotFittingStore.ReadModuleAttributes(
            world,
            catalog,
            pilotId,
            PilotInventoryStore.Read(catalog, module),
            calculation
        );
        var repairer = LocalRepairRules.Define(moduleId, module.TypeId, attributes, layer);
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt)
        {
            throw new InvalidDataException("The repairer is still cycling.");
        }
        if (world.Set<PendingLocalRepair>().Find(moduleId) is { } prior && prior.ShipId != pilot.ActiveShipId)
        {
            if (prior.CompletesAt > now)
            {
                throw new InvalidDataException("The repairer still has a paid cycle on its previous ship.");
            }
            // An unfitted module keeps its paid completion on the original hull.
            // Settle that debt before the same module can start on another ship.
            var previous = world.Ships.Find(prior.ShipId)!;
            var owner = world.Pilots.Single(p => p.Id == previous.OwnerId);
            var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, owner, previous));
            _ = PilotHealthStore.Read(world, previous.Id, defense, now);
        }
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        var health = PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now);
        if (health.Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot run a repairer.");
        }
        var fuel = ancillary
            ? AncillaryFuelPersistence.Consume(
                world,
                catalog,
                pilotId,
                module,
                AncillaryRepairRules.Define(AncillaryRepairKind.Armor, attributes)
            )
            : null;
        if (fuel is not null)
        {
            repairer = repairer with { Amount = fuel.Payment.Amount, CapacitorNeed = fuel.Payment.CapacitorNeed };
        }
        long completes = LocalRepairRules.CompletesAt(now, repairer);
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, repairer.CapacitorNeed, now);
        if (world.Set<ShipHealthState>().Find(pilot.ActiveShipId) is null)
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = pilot.ActiveShipId,
                    ShieldCharge = health.Remaining.Shield,
                    UpdatedAt = now,
                }
            );
        }
        world.Add(
            new PendingLocalRepair
            {
                ModuleId = moduleId,
                ShipId = pilot.ActiveShipId,
                StartedAt = now,
                CompletesAt = completes,
                Amount = repairer.Amount,
                Layer = layer,
            }
        );
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = completes;
        var heat = calculation is null ? null : ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation, moduleId, now, completes);
        world.SaveChanges();
        transaction.Commit();
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, pilot.ActiveShipId, heat);
        }
        return new(new(moduleId, module.TypeId, now, completes, layer, ancillary), capacitor, fuel?.Magazine);
    }

    public IReadOnlyList<LocalRepairSchedule> Read(long pilotId, long shipId, long now)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var schedules = world
            .Set<PendingLocalRepair>()
            .Where(repair => repair.ShipId == shipId && repair.CompletesAt > now)
            .AsEnumerable()
            .Select(repair =>
            {
                var item = world.Set<OwnedItem>().Find(repair.ModuleId);
                bool valid =
                    item is not null
                    && item.OwnerId == pilotId
                    && item.LocationId == shipId
                    && item.Online
                    && (
                        repair.Layer == RepairLayer.Armor
                            ? InventoryDefinitions.IsLowSlot(item.FlagId)
                            : repair.Layer == RepairLayer.Hull && InventoryDefinitions.IsMediumSlot(item.FlagId)
                    );
                return valid ? new LocalRepairSchedule(item!.Id, item.TypeId, repair.StartedAt, repair.CompletesAt, repair.Layer) : null;
            })
            .OfType<LocalRepairSchedule>()
            .ToArray();
        return schedules
            .Select(s => s with { Ancillary = catalog.FittingType(s.TypeId)?.GroupId == AncillaryRepairRules.ArmorGroup })
            .ToArray();
    }

    public ShipHealth Complete(long pilotId, long shipId, long now)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Single(p => p.Id == pilotId && p.ActiveShipId == shipId);
        var health = PilotHealthStore.Read(
            world,
            shipId,
            ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilot)),
            now
        );
        transaction.Commit();
        return health;
    }

    private TransactionParticipants LocalParticipants(long pilot, long module)
    {
        long? previous = LiveWorld.Find(database.Path)?.Economy.Inspect(world => world.Set<PendingLocalRepair>().Find(module)?.ShipId);
        return new(pilots: [pilot], ships: previous is long ship ? [ship] : []);
    }
}
