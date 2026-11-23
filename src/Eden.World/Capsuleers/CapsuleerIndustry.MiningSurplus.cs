using Eden.Data.World.Contracts;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private readonly record struct OreSaleStock(long Id, int TypeId, int Quantity);

    internal bool SellMiningSurplus(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, long now)
    {
        if (
            !StationCapabilities.Has(catalog, pilot.OperationId, StationService.Market)
            || HasStationFactoryJob(world, pilot.PilotId, pilot.StationId)
            || HasWorkDemand(world, catalog, pilot, now)
        )
        {
            return false;
        }
        return SellHangarOre(world, catalog, pilot);
    }

    private bool SellHangarOre(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot)
    {
        long owner = pilot.PilotId;
        int station = pilot.StationId;
        var items = (EconomySet<OwnedItem, EconomyOwnedItem>)world.Set<OwnedItem>();
        var escrow = world.Set<SavedContractEscrow>();
        var stock = items
            .IndexedSnapshots(nameof(OwnedItem.OwnerId), owner)
            .Where(item =>
                item.OwnerId == owner
                && item.LocationId == station
                && item.FlagId == (int)InventoryFlag.Hangar
                && catalog.FittingType(item.TypeId)?.CategoryId == AsteroidDefinitions.Category
                && escrow.Find(item.Id) is null
            )
            .Select(item => new OreSaleStock(item.Id, item.TypeId, item.Quantity))
            .OrderBy(item => item.Id)
            .ToArray();
        return SellOre(world, catalog, pilot, stock);
    }

    private bool SellOre(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, IEnumerable<OreSaleStock> stock)
    {
        int station = pilot.StationId;
        Dictionary<int, bool> supportedTypes = [];
        foreach (var item in stock)
        {
            if (!supportedTypes.TryGetValue(item.TypeId, out bool supported))
            {
                supported = CapsuleerProvisioningCatalog.SupportedOre(catalog, item.TypeId);
                supportedTypes.Add(item.TypeId, supported);
            }
            if (!supported)
            {
                continue;
            }
            var result = market.ExecuteSell(
                world,
                catalog,
                pilot.PilotId,
                [new(item.Id, station, item.TypeId, 1, item.Quantity)],
                0,
                skipUnmatched: true
            );
            if (result.Trades.Count > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static long WalletBalance(WorldAction world, long pilot) => world.Set<PilotWallet>().Find(pilot)?.Cents ?? 0;
}
