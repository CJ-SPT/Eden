using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private static Collection? PlanMoonCollection(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        SavedCorporation employer,
        long factory,
        FactorySupplyRequest request,
        FactoryMoonSupply supply,
        IReadOnlySet<int> avoided,
        double free
    )
    {
        int maximum = CapsuleerIndustryRules.MaximumRefinerySupplyJumps;
        if (
            Employer(world, supply.OperatorId, employer.Id) is null
            || MoonPickup(world, catalog, supply, free).Length == 0
            || CapsuleerTrafficRoutes.ToStation(
                catalog,
                supply.StationId,
                request.StationId,
                HullClass.Hauler,
                avoided,
                maximum,
                policy: world.Settings.Capsuleers
            )
                is null
            || CapsuleerTrafficRoutes.ToStation(
                catalog,
                courier.StationId,
                supply.StationId,
                HullClass.Hauler,
                avoided,
                maximum,
                policy: world.Settings.Capsuleers
            )
                is not { } route
        )
        {
            return null;
        }
        var shipment = new FactorySupplyShipment(factory, request.StationId, supply.StationId, request.Demand?.OrderId)
        {
            MoonSupply = supply,
        };
        return new(new(employer.Id, 0) { FactorySupply = shipment }, route);
    }

    private static CapsuleerFactoryCargo[] MoonPickup(
        EconomyTransaction world,
        CatalogDbContext catalog,
        FactoryMoonSupply supply,
        double free
    ) => MoonCargo(catalog, supply.Materials, [], Hangar(world, supply.OperatorId, supply.StationId), free);

    private static FreightManifest? CollectMoonCargo(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        FactorySupplyShipment shipment,
        FactoryMoonSupply supply
    )
    {
        if (courier.StationId != supply.StationId || Employer(world, supply.OperatorId, courier.CorpId) is null)
        {
            return null;
        }
        var load = MoonPickup(world, catalog, supply, FreeFreightVolume(world, catalog, courier));
        List<FreightStack> cargo = [];
        foreach (var part in load)
        {
            var item = world.Set<OwnedItem>().Find(part.ItemId)!;
            int type = item.TypeId;
            if (part.Quantity < item.Quantity)
            {
                item.Quantity -= part.Quantity;
                item = world
                    .Add(
                        new OwnedItem
                        {
                            Id = world.NextItemId(),
                            TypeId = type,
                            Quantity = part.Quantity,
                            VolumeOverride = item.VolumeOverride,
                        }
                    )
                    .Entity;
            }
            item.OwnerId = courier.PilotId;
            item.LocationId = courier.ShipId;
            item.FlagId = (int)InventoryFlag.Cargo;
            cargo.Add(new(item.Id, type, part.Quantity));
        }
        return cargo.Count == 0 ? null : new(0, shipment.FactoryStationId, cargo.ToArray());
    }
}
