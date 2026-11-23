using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Planetary;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    internal sealed record FactorySupplyPreparation(int[] Warehouses, int[] PlanetaryWarehouses, bool Research);

    internal static FactorySupplyPreparation? CaptureFactorySupplies(
        EconomyTransaction world,
        CatalogDbContext catalog,
        LiveWorld live,
        long pilot,
        int shipType,
        HaulerProgress? work,
        long now
    )
    {
        if (work?.CorporationSupply is { } supply)
        {
            return supply.FactorySupply is { } shipment ? new([shipment.FactoryStationId], [], shipment.Research is not null) : null;
        }
        if (
            catalog.FittingType(shipType)?.GroupId != (int)HullClass.Hauler
            || world.Pilots.Find(pilot) is not { } courier
            || Employer(world, pilot, courier.CorporationId) is not { BalanceCents: > 0 } employer
            || FactoryRequests(world, live, employer.Id, now) is not { Length: > 0 } requests
        )
        {
            return null;
        }
        var active = requests.Select(entry => entry.Request!).ToArray();
        var warehouses = active.Select(request => request.StationId).Distinct().Order().ToArray();
        var planetary =
            work is { PlanetaryProduction: null } && work.PlanetaryRetryAt <= now
                ? active
                    .Where(request => request.Materials.Any(need => PlanetaryMaterial(catalog, need.TypeId)))
                    .Select(request => request.StationId)
                    .Distinct()
                    .Order()
                    .ToArray()
                : [];
        return new(warehouses, planetary, active.Any(request => request.ResearchDatacores));
    }

    internal static void PrepareFactorySupplies(
        CatalogDbContext catalog,
        FactorySupplyPreparation preparation,
        CapsuleerPopulationPolicy policy
    )
    {
        using var phase = SessionPerformance.Phase("traffic.prepareFactorySupplies");
        if (preparation.Research)
        {
            _ = catalog.GlobalDefinition(s_researchSuppliers);
        }
        if (preparation.PlanetaryWarehouses.Length == 0)
        {
            return;
        }

        var staging = catalog.GlobalDefinition(s_planetaryStaging);
        HashSet<int> noAvoidedSystems = [];
        var systems = preparation
            .PlanetaryWarehouses.SelectMany(warehouse =>
                PlanetaryStagingSystems(catalog, warehouse, policy, staging.MarketOperations, noAvoidedSystems)
            )
            .Select(stop => stop.System)
            .Distinct()
            .ToArray();
        _ = PlanetaryCatalogReader.ProductionSites(catalog, systems);
    }

    private static bool PlanetaryMaterial(CatalogDbContext catalog, int type) =>
        catalog.FittingType(type)?.CategoryId is (int)InventoryCategory.PlanetaryCommodity or (int)InventoryCategory.PlanetaryResource;
}
