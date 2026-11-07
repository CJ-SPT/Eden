using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Repair;

[Injectable]
internal sealed class PilotSupportModuleStore(WorldDatabase database) : IPilotSupportModules
{
    public SupportActivation Activate(long pilotId, long moduleId, DogmaEffect effect, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var module = world.Set<OwnedItem>().Find(moduleId);
        int group = module is null ? 0 : catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).GroupId;
        if (
            pilot.ActiveShip.Position is null
            || module is null
            || module.OwnerId != pilotId
            || module.LocationId != pilot.ActiveShipId
            || !module.Singleton
            || !module.Online
            || !InventoryDefinitions.IsMediumSlot(module.FlagId)
            || SupportModuleDefinitions.Effect(group) != effect
            || !SupportModuleCatalog.Supported(catalog, module.TypeId, group)
        )
        {
            throw new InvalidDataException("Use an online fitted support module on your active ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        if (PilotAmmunitionStore.Contents(world, moduleId) is { } loaded)
        {
            int chargeGroup = catalog.Set<ItemType>().Single(t => t.Id == loaded.TypeId).GroupId;
            if (
                loaded.OwnerId != pilotId
                || loaded.Quantity != 1
                || !ScriptCatalog.Supported(catalog, loaded.TypeId, chargeGroup)
                || !SupportModuleDefinitions.CompatibleScript(
                    group,
                    chargeGroup,
                    PilotFittingStore.ReadAttributes(catalog, module.TypeId),
                    PilotFittingStore.ReadAttributes(catalog, loaded.TypeId)
                )
            )
            {
                throw new InvalidDataException("Load a compatible support module script.");
            }
        }
        var calculation = FittingCalculation.Read(world, catalog, pilot);
        var support = SupportModuleDefinitions.Define(moduleId, module.TypeId, group, calculation.Graph.Attributes(moduleId));
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt)
        {
            throw new InvalidDataException("The support module is still cycling or loading a script.");
        }
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate a support module.");
        }
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, support.CapacitorNeed, now);
        if (world.Set<ActiveFittedModule>().Find(moduleId) is null)
        {
            world.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = pilot.ActiveShipId });
        }
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(now + (long)Math.Ceiling(support.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        world.SaveChanges();
        transaction.Commit();
        return new(support, capacitor);
    }

    public void Stop(long pilotId, long shipId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        if (world.Ships.Any(s => s.Id == shipId && s.OwnerId == pilotId))
        {
            world.Set<ActiveFittedModule>().Where(a => a.ModuleId == moduleId && a.ShipId == shipId).ExecuteDelete();
        }
    }
}
