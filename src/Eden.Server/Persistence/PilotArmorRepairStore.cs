using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class PendingArmorRepair
{
    public long ModuleId { get; set; }
    public long ShipId { get; set; }
    public long StartedAt { get; set; }
    public long CompletesAt { get; set; }
    public double Amount { get; set; }
}

[Injectable]
internal sealed class PilotArmorRepairStore(WorldDatabase database) : IPilotArmorRepairing
{
    public ArmorRepairActivation Start(long pilotId, long moduleId, long now)
    {
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
            || !InventoryDefinitions.IsLowSlot(module.FlagId)
            || catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).GroupId != ArmorRepairDefinitions.Group
            || FittingCatalog.Definition(catalog, module.TypeId) is null
        )
        {
            throw new InvalidDataException("Use an online fitted Tech I armor repairer on your active ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        var attributes = PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, PilotInventoryStore.Read(catalog, module));
        var repairer = ArmorRepairRules.Define(moduleId, module.TypeId, attributes);
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt)
        {
            throw new InvalidDataException("The armor repairer is still cycling.");
        }
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        var health = PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now);
        if (health.Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot repair its armor.");
        }
        long completes = checked(now + (long)Math.Ceiling(repairer.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
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
            new PendingArmorRepair
            {
                ModuleId = moduleId,
                ShipId = pilot.ActiveShipId,
                StartedAt = now,
                CompletesAt = completes,
                Amount = repairer.ArmorAmount,
            }
        );
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = completes;
        world.SaveChanges();
        transaction.Commit();
        return new(new(moduleId, module.TypeId, now, completes), capacitor);
    }

    public IReadOnlyList<ArmorRepairSchedule> Read(long pilotId, long shipId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var schedules = (
            from repair in world.Set<PendingArmorRepair>()
            join item in world.Set<OwnedItem>() on repair.ModuleId equals item.Id
            where
                repair.ShipId == shipId
                && item.OwnerId == pilotId
                && item.LocationId == shipId
                && item.Online
                && item.FlagId >= (int)InventoryFlag.LowSlot0
                && item.FlagId <= (int)InventoryFlag.LowSlot7
                && repair.CompletesAt > now
            select new ArmorRepairSchedule(item.Id, item.TypeId, repair.StartedAt, repair.CompletesAt)
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
