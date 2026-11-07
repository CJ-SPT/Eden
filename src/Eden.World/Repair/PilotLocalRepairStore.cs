using Eden.Data.Catalog;
using Eden.Data.Combat;
using Eden.Data.Inventory;
using Eden.Data.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Repair;

[Injectable]
internal sealed class PilotLocalRepairStore(WorldDatabase database) : IPilotLocalRepairing
{
    public LocalRepairActivation Start(long pilotId, long moduleId, long now, RepairLayer layer = RepairLayer.Armor)
    {
        var profile = LocalRepairDefinitions.Profile(layer);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
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
            || catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).GroupId != profile.GroupId
            || FittingCatalog.Definition(catalog, module.TypeId) is null
        )
        {
            throw new InvalidDataException("Use an online fitted local repairer of the requested kind on your active ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        var attributes = PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, PilotInventoryStore.Read(catalog, module));
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
            var owner = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == previous.OwnerId);
            var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, owner, previous));
            _ = PilotHealthStore.Read(world, previous.Id, defense, now);
        }
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        var health = PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now);
        if (health.Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot run a repairer.");
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
        world.SaveChanges();
        transaction.Commit();
        return new(new(moduleId, module.TypeId, now, completes, layer), capacitor);
    }

    public IReadOnlyList<LocalRepairSchedule> Read(long pilotId, long shipId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var schedules = (
            from repair in world.Set<PendingLocalRepair>()
            join item in world.Set<OwnedItem>() on repair.ModuleId equals item.Id
            where
                repair.ShipId == shipId
                && item.OwnerId == pilotId
                && item.LocationId == shipId
                && item.Online
                && (
                    (
                        repair.Layer == RepairLayer.Armor
                        && item.FlagId >= (int)InventoryFlag.LowSlot0
                        && item.FlagId <= (int)InventoryFlag.LowSlot7
                    )
                    || (
                        repair.Layer == RepairLayer.Hull
                        && item.FlagId >= (int)InventoryFlag.MediumSlot0
                        && item.FlagId <= (int)InventoryFlag.MediumSlot7
                    )
                )
                && repair.CompletesAt > now
            select new LocalRepairSchedule(item.Id, item.TypeId, repair.StartedAt, repair.CompletesAt, repair.Layer)
        ).ToArray();
        return schedules;
    }

    public ShipHealth Complete(long pilotId, long shipId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId && p.ActiveShipId == shipId);
        var health = PilotHealthStore.Read(
            world,
            shipId,
            ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilot)),
            now
        );
        transaction.Commit();
        return health;
    }
}
