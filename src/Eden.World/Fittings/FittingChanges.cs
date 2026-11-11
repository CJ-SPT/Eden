using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Inventory;
using Eden.World.Storage;

namespace Eden.World.Fittings;

internal sealed record FittingCheckpoint(
    ShipResourceSnapshot Resources,
    IReadOnlyDictionary<long, InventoryItem> Modules,
    HullFittingCapacity Capacity
);

internal static partial class FittingChanges
{
    internal static FittingCheckpoint Capture(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, long now)
    {
        var calculation = FittingCalculation.Read(world, catalog, pilot);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        return new(
            new(
                PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(attributes), now),
                ShipResources.Capacitor(world, pilot.ActiveShipId, attributes, now),
                attributes
            ),
            PilotInventoryStore
                .List(world, catalog, pilot.Id, pilot.ActiveShipId)
                .Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId))
                .ToDictionary(i => i.Id),
            HullFittingCapacity.Read(calculation.Graph.Attributes(pilot.ActiveShipId))
        );
    }

    internal static IReadOnlyList<InventoryChange> Finish(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot pilot,
        FittingCheckpoint before,
        long now
    )
    {
        var changed = new List<InventoryChange>();
        Dictionary<DogmaAttributeId, double> attributes;
        while (true)
        {
            var calculation = FittingCalculation.Read(world, catalog, pilot);
            var constraints = ReadConstraints(catalog, calculation);
            ModuleFittingConstraints.CheckFitted(constraints);
            FittingSlotRules.Check(
                HullFittingCapacity.Read(calculation.Graph.Attributes(pilot.ActiveShipId)),
                calculation.ResourceUsage(catalog)
            );
            attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
            long? shutdown =
                ModuleFittingConstraints.ShutdownOnlineExcess(constraints)
                ?? FittingResourceRules.Shutdown(
                    attributes,
                    calculation
                        .Modules.Where(m => m.Item.Online)
                        .Select(m => new FittingShutdownCandidate(
                            m.Item.Id,
                            calculation.Graph.Read(m.Item.Id, DogmaAttributeId.Cpu),
                            calculation.Graph.Read(m.Item.Id, DogmaAttributeId.Power)
                        ))
                        .ToArray()
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
        double used = InventoryPersistence.UsedVolume(world, catalog, pilot.Id, pilot.ActiveShipId, (int)InventoryFlag.Cargo);
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
            var health =
                world.Set<ShipHealthState>().Find(pilot.ActiveShipId)
                ?? world.Set<ShipHealthState>().Add(new() { ShipId = pilot.ActiveShipId }).Entity;
            health.ShieldCharge = resized.Shield;
            health.ArmorDamage = resized.ArmorDamage;
            health.StructureDamage = resized.StructureDamage;
            health.UpdatedAt = Math.Max(now, health.UpdatedAt);
            var energy =
                world.Set<ShipEnergy>().Find(pilot.ActiveShipId)
                ?? world.Set<ShipEnergy>().Add(new() { ShipId = pilot.ActiveShipId }).Entity;
            energy.Charge = resized.Capacitor;
            energy.UpdatedAt = Math.Max(now, energy.UpdatedAt);
        }
        world.SaveChanges();
        PilotWeaponBankStore.Prune(world, pilot.ActiveShipId);
        world.SaveChanges();
        return changed;
    }

    internal static IReadOnlyList<FittedModuleConstraints> ReadConstraints(CatalogDbContext catalog, FittingCalculation calculation) =>
        calculation
            .Modules.Select(module =>
            {
                var limits = FittingModuleLimits.Read(calculation.Graph.Attributes(module.Item.Id));
                // The native location item checks type attribute presence before
                // reading its effective online limit, including offline modules.
                if (!catalog.FittingAttributes(module.Item.TypeId).ContainsKey(DogmaAttributeId.MaxGroupOnline))
                {
                    limits = limits with { MaximumOnline = null };
                }
                return new FittedModuleConstraints(module.Item.Id, module.Item.TypeId, module.GroupId, module.Item.Online, limits);
            })
            .ToArray();
}
