using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Fittings;

internal sealed partial class PilotFittingStore
{
    internal static bool CanOnlineFit(WorldAction world, CatalogDbContext catalog, long pilotId, ReplacementKit kit)
    {
        if (world.Pilots.Find(pilotId) is not { } pilot || catalog.FittingType(kit.HullTypeId) is null)
        {
            return false;
        }

        // These IDs exist only in the computation. They never enter the world or normal fitting cache.
        const long previewShipId = long.MinValue + 1;
        var ship = new Ship
        {
            Id = previewShipId,
            OwnerId = pilotId,
            TypeId = kit.HullTypeId,
            StationId = pilot.ActiveShip.StationId,
        };
        var modules = kit
            .Slots.Select(
                (slot, index) =>
                    new OwnedItem
                    {
                        Id = previewShipId + 1 + index,
                        OwnerId = pilotId,
                        LocationId = previewShipId,
                        TypeId = slot.TypeId,
                        FlagId = slot.FlagId,
                        Quantity = 1,
                        Singleton = true,
                        Online = !InventoryDefinitions.IsRigSlot(slot.FlagId) && kit.OnlineSlots.Contains(slot.FlagId),
                    }
            )
            .ToArray();

        try
        {
            // The owner fits the rack before loading charges. Snapshot validation already checks its slot and charge types.
            var calculation = FittingCalculation.Read(world, catalog, pilot, ship, proposedModules: modules);
            FittingSlotRules.Check(HullFittingCapacity.Read(calculation.Graph.Attributes(ship.Id)), calculation.ResourceUsage(catalog));
            var constraints = FittingChanges.ReadConstraints(catalog, calculation);
            ModuleFittingConstraints.CheckFitted(constraints);
            ModuleFittingConstraints.CheckOnline(constraints);

            var attributes = ShipAttributes(world, catalog, pilot, ship, calculation);
            var resources = modules
                .Where(module => module.Online)
                .Select(module => new FittingShutdownCandidate(
                    module.Id,
                    calculation.Graph.Read(module.Id, DogmaAttributeId.Cpu),
                    calculation.Graph.Read(module.Id, DogmaAttributeId.Power)
                ))
                .ToArray();
            return FittingResourceRules.Shutdown(attributes, resources) is null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
