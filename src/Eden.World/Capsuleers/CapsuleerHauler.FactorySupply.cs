using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Ships;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool HandleFactorySupply(EconomyTransaction world, LiveWorld live, CorporationSupplyRun run, FactorySupplyShipment shipment)
    {
        if (shipment.Refinery is { } refinery)
        {
            return HandleRefinerySupply(world, run, shipment, refinery);
        }
        if (shipment.Planetary is { } visit)
        {
            return HandlePlanetarySupply(world, run, shipment, visit);
        }
        var catalog = _operation.Catalog;
        var freight = Work.Freight;
        if (Pilot.StationId == shipment.FactoryStationId && freight is not null)
        {
            CapsuleerIndustry.Deliver(world, catalog, Pilot, run, freight);
            return CompleteFactorySupply();
        }
        if (freight is null && Pilot.StationId != shipment.SupplierStationId)
        {
            // An outbound retreat reached a dock before the purchase.
            return CompleteFactorySupply();
        }

        var delivery = CapsuleerTrafficRoutes.ToStation(
            catalog,
            Pilot.StationId,
            shipment.FactoryStationId,
            (HullClass)Pilot.ShipGroupId,
            AvoidedSupplierSystems(),
            shipment.MaximumJumps
        );
        if (delivery is null)
        {
            if (freight is null)
            {
                return CompleteFactorySupply();
            }
            // Keep paid cargo in its hold while temporarily avoiding a dangerous route.
            _work = Work with
            {
                DueTicks = Work.ActiveTicks + TimeSpan.FromSeconds(HaulingRules.PollSeconds).Ticks,
            };
            return true;
        }
        if (freight is null)
        {
            freight = ammunitionSupply.PurchaseFactoryMaterials(world, catalog, live, Pilot, run, shipment, Now);
            if (freight is null)
            {
                return CompleteFactorySupply();
            }
            _operation.MarketChanged |= shipment.Research is null && shipment.MoonSupply is null;
            _work = Work with { Freight = freight };
        }
        BeginSupplyJourney(delivery);
        return true;
    }

    private bool CompleteFactorySupply()
    {
        _work = Work with
        {
            CorporationSupply = null,
            Freight = null,
            Journey = null,
            DueTicks = Work.ActiveTicks + TimeSpan.TicksPerSecond,
        };
        return true;
    }
}
