using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Fittings;

internal static partial class FittingChanges
{
    internal static FittingCheckpoint Capture(WorldAction world, CatalogDbContext catalog, Pilot pilot, long now, Ship? ship = null)
    {
        ship ??= pilot.ActiveShip;
        var calculation = FittingCalculation.Read(world, catalog, pilot, ship);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot, ship, calculation);
        return new(
            new(
                PilotHealthStore.Read(world, ship.Id, ShipHealth.DefenseFrom(attributes), now),
                ShipResources.Capacitor(world, ship.Id, attributes, now),
                attributes
            ),
            PilotInventoryStore
                .List(world, catalog, pilot.Id, ship.Id)
                .Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId))
                .ToDictionary(i => i.Id),
            HullFittingCapacity.Read(calculation.Graph.Attributes(ship.Id))
        );
    }

    internal static IReadOnlyList<InventoryChange> Finish(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        FittingCheckpoint before,
        long now,
        Ship? ship = null,
        IReadOnlySet<long>? preferShutdown = null
    )
    {
        ship ??= pilot.ActiveShip;
        var changed = new List<InventoryChange>();
        Dictionary<DogmaAttributeId, double> attributes;
        while (true)
        {
            var calculation = FittingCalculation.Read(world, catalog, pilot, ship);
            var constraints = ReadConstraints(catalog, calculation);
            ModuleFittingConstraints.CheckFitted(constraints);
            FittingSlotRules.Check(HullFittingCapacity.Read(calculation.Graph.Attributes(ship.Id)), calculation.ResourceUsage(catalog));
            attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot, ship, calculation);
            long? shutdown =
                ModuleFittingConstraints.ShutdownOnlineExcess(constraints, preferShutdown)
                ?? FittingResourceRules.Shutdown(
                    attributes,
                    calculation
                        .Modules.Where(m => m.Item.Online)
                        .Select(m => new FittingShutdownCandidate(
                            m.Item.Id,
                            calculation.Graph.Read(m.Item.Id, DogmaAttributeId.Cpu),
                            calculation.Graph.Read(m.Item.Id, DogmaAttributeId.Power)
                        ))
                        .ToArray(),
                    preferShutdown
                );
            if (shutdown is null)
            {
                break;
            }
            var candidate = calculation.Modules.Single(m => m.Item.Id == shutdown.Value);
            var previous = before.Modules.GetValueOrDefault(candidate.Item.Id) ?? PilotInventoryStore.Read(catalog, candidate.Item);
            candidate.Item.Online = false;
            changed.Add(new(PilotInventoryStore.Read(catalog, candidate.Item), previous));
        }
        double used = InventoryPersistence.UsedVolume(world, catalog, pilot.Id, ship.Id, (int)InventoryFlag.Cargo);
        var old = before.Resources;
        var resized = FittingResourceRules.Resize(old, attributes, used);
        DogmaAttributeId[] resources =
        [
            DogmaAttributeId.ShieldCapacity,
            DogmaAttributeId.ArmorHitPoints,
            DogmaAttributeId.StructureHitPoints,
            DogmaAttributeId.ShieldRechargeTime,
            DogmaAttributeId.CapacitorCapacity,
            DogmaAttributeId.CapacitorRechargeTime,
        ];
        if (resources.Any(a => old.Attributes.GetValueOrDefault(a) != attributes.GetValueOrDefault(a)))
        {
            var health = world.Set<ShipHealthState>().Find(ship.Id) ?? world.Set<ShipHealthState>().Add(new() { ShipId = ship.Id }).Entity;
            health.ShieldCharge = resized.Shield;
            health.ArmorDamage = resized.ArmorDamage;
            health.StructureDamage = resized.StructureDamage;
            health.UpdatedAt = Math.Max(now, health.UpdatedAt);
            var energy = world.Set<ShipEnergy>().Find(ship.Id) ?? world.Set<ShipEnergy>().Add(new() { ShipId = ship.Id }).Entity;
            energy.Charge = resized.Capacitor;
            energy.UpdatedAt = Math.Max(now, energy.UpdatedAt);
        }
        world.SaveChanges();
        PilotWeaponBankStore.Prune(world, ship.Id);
        world.SaveChanges();
        return changed;
    }
}
