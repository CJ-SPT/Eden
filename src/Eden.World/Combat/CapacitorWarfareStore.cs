using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

[Injectable]
internal sealed class CapacitorWarfareStore(WorldDatabase database, ICapacitorEvents events) : IPilotCapacitorWarfare
{
    public bool IsOnline(long pilotId, long shipId, long moduleId) =>
        LiveReadModels.Find(database.Path)?.CapacitorModuleOnline(pilotId, shipId, moduleId, ReadOnline)
        ?? ReadOnline(pilotId, shipId, moduleId);

    private bool ReadOnline(long pilot, long ship, long module)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<OwnedItem>()
            .Any(i =>
                i.Id == module
                && i.OwnerId == pilot
                && i.LocationId == ship
                && i.Singleton
                && i.Online
                && i.FlagId >= (int)InventoryFlag.HighSlot0
                && i.FlagId <= (int)InventoryFlag.HighSlot7
            );
    }

    public CapacitorActivation Activate(long pilotId, long moduleId, DogmaEffect effect, long targetId, IReadOnlySet<long> locks, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var source = Pilot(world, pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        int group = item is null ? 0 : catalog.FittingType(item.TypeId)?.GroupId ?? 0;
        if (
            source.ActiveShip.Position is null
            || item is null
            || item.OwnerId != pilotId
            || item.LocationId != source.ActiveShipId
            || !item.Singleton
            || !item.Online
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || !CapacitorModuleCatalog.Supported(catalog, item.TypeId, group)
            || CapacitorWarfareRules.Effect(group) != effect
        )
        {
            throw new InvalidDataException("Use an online fitted capacitor module on your active ship in space.");
        }
        if (targetId == source.ActiveShipId || !locks.Contains(targetId))
        {
            throw new InvalidDataException("Lock another nearby pilot's ship before applying a capacitor module.");
        }
        var target = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.ActiveShipId == targetId);
        int system = CrimewatchPersistence.System(catalog, source.ActiveShip);
        if (
            target?.ActiveShip.Position is null
            || CrimewatchPersistence.System(catalog, target.ActiveShip) != system
            || world.Set<SavedLogoff>().Any(l => l.ShipId == targetId && l.ExpiresAt <= now)
        )
        {
            throw new InvalidDataException("The capacitor target must be another pilot's live ship in the same system.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        var calculation = FittingCalculation.Read(world, catalog, source);
        var module = CapacitorWarfareRules.Define(item.Id, item.TypeId, effect, calculation.Graph.Attributes(item.Id));
        var sourceAttributes = PilotFittingStore.ShipAttributes(world, catalog, source, calculation: calculation);
        var targetAttributes = PilotFittingStore.ShipAttributes(world, catalog, target);
        CheckAlive(source, sourceAttributes);
        CheckAlive(target, targetAttributes);
        bool transfer = module.Kind == CapacitorModuleKind.Transfer;
        if (
            targetAttributes.GetValueOrDefault(transfer ? DogmaAttributeId.DisallowAssistance : DogmaAttributeId.DisallowOffensiveModifiers)
            != 0
        )
        {
            throw new InvalidDataException("That ship cannot receive this capacitor effect.");
        }
        var a = source.ActiveShip.Position;
        var b = target.ActiveShip.Position;
        double distance = Math.Max(
            0,
            new SpacePosition(a.X, a.Y, a.Z).DistanceTo(new(b.X, b.Y, b.Z))
                - (catalog.FittingType(source.ActiveShip.TypeId)!.Radius ?? 0)
                - (catalog.FittingType(target.ActiveShip.TypeId)!.Radius ?? 0)
        );
        if (
            distance > Math.Min(TackleDefinitions.MaximumDistance, sourceAttributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
            || distance > module.Optimal && module.Falloff == 0
        )
        {
            throw new InvalidDataException("The capacitor target is outside range.");
        }
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown?.ReadyAt > now)
        {
            throw new InvalidDataException("The capacitor module is still cycling.");
        }
        long end = checked(now + (long)Math.Ceiling(module.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        if (transfer)
        {
            AssistancePersistence.Apply(world, catalog, source, target, now, end);
        }
        else
        {
            var decision = CrimewatchPersistence.CheckPlayer(world, catalog, source, target, system, now);
            CrimewatchPersistence.Player(world, source.Id, target.Id, system, decision, now, end);
            Eden.World.Drones.DroneCombatPersistence.Module(world, source.ActiveShipId, targetId, true, now);
        }
        var applied = CapacitorWarfareRules.Apply(
            module,
            new(
                ShipResources.Capacitor(world, source.ActiveShipId, sourceAttributes, now),
                sourceAttributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity)
            ),
            new(
                ShipResources.Capacitor(world, targetId, targetAttributes, now),
                targetAttributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity)
            ),
            distance,
            targetAttributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            targetAttributes.GetValueOrDefault(
                transfer ? DogmaAttributeId.RemoteCapacitorImpedance : DogmaAttributeId.EnergyWarfareResistance,
                1
            )
        );
        SaveEnergy(source.ActiveShipId, applied.Source);
        SaveEnergy(targetId, applied.Target);
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = end;
        if (world.Set<ActiveFittedModule>().Find(moduleId) is null)
        {
            world.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = source.ActiveShipId });
        }
        world.SaveChanges();
        transaction.Commit();
        events.Publish(new(source.Id, source.ActiveShipId));
        events.Publish(new(target.Id, targetId));
        return new(new(module, source.ActiveShipId, targetId, now, end), applied.Source, applied.Target, applied.Amount);

        void CheckAlive(Pilot pilot, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
        {
            if (
                PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed
                || world.Set<ShipLoss>().Any(l => l.ShipId == pilot.ActiveShipId)
            )
            {
                throw new InvalidDataException("A destroyed ship cannot send or receive capacitor effects.");
            }
        }
        void SaveEnergy(long ship, double charge)
        {
            var saved = world.Set<ShipEnergy>().Find(ship) ?? world.Add(new ShipEnergy { ShipId = ship }).Entity;
            saved.Charge = charge;
            saved.UpdatedAt = Math.Max(saved.UpdatedAt, now);
        }
    }

    private static Pilot Pilot(WorldDbContext world, long id) =>
        world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == id);

    public void Stop(long pilotId, long shipId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        if (world.Set<OwnedItem>().Any(i => i.Id == moduleId && i.OwnerId == pilotId && i.LocationId == shipId))
        {
            world.Set<ActiveFittedModule>().Where(i => i.ModuleId == moduleId && i.ShipId == shipId).ExecuteDelete();
        }
    }
}
