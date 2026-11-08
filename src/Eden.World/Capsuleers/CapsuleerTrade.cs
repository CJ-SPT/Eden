using Eden.Data.World.Capsuleers;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable]
internal sealed class CapsuleerTrade(WorldDatabase database, PilotMarketStore market, MarketChangeSignal signal)
{
    internal bool Enabled()
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<SavedCapsuleerHauler>().Any();
    }

    internal void ListStock(long pilot, int station, decimal multiplier = 1)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        bool changed = ListStock(world, catalog, market, pilot, station, multiplier);
        transaction.Commit();
        if (changed)
        {
            signal.Changed();
        }
    }

    internal static bool ListStock(
        WorldDbContext world,
        CatalogDbContext catalog,
        PilotMarketStore marketStore,
        long pilot,
        int station,
        decimal multiplier = 1
    )
    {
        bool changed = marketStore.Expire(world, catalog) > 0;
        var existing = world
            .Set<SavedPersonalOrder>()
            .Where(o => o.PilotId == pilot && o.Status == PersonalOrderStatus.Open)
            .AsEnumerable()
            .Select(o => o.Snapshot())
            .ToArray();
        if (existing.Length >= PersonalMarketRules.OrderLimit(0, 0, 0, 0))
        {
            return changed;
        }
        var stock = PilotInventoryStore
            .List(world, catalog, pilot, station)
            .Where(i =>
                i.CategoryId == AsteroidDefinitions.Category
                && i.FlagId == (int)InventoryFlag.Hangar
                && !existing.Any(o => !o.Bid && o.TypeId == i.TypeId && o.StationId == station)
            )
            .OrderBy(i => i.Id)
            .FirstOrDefault();
        if (stock is null)
        {
            return changed;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)stock.TypeId);
        long price = PersonalMarketRules.Round(
            HaulingRules.OrePrice(type?.BasePrice ?? 0, checked((int)(type?.PortionSize ?? 1))) * multiplier
        );
        if (price <= 0)
        {
            return changed;
        }
        int quantity = (int)Math.Min(stock.Quantity, PersonalMarketRules.Round(HaulingRules.MaximumLoadCents * multiplier) / price);
        if (quantity <= 0 || (world.Set<PilotWallet>().Find(pilot)?.Cents ?? 0) < PersonalMarketRules.BrokerFee(price, quantity, 0.03m))
        {
            return changed;
        }
        marketStore.ExecuteSell(world, catalog, pilot, [new(stock.Id, station, stock.TypeId, price, quantity)], HaulingRules.OrderDays);
        return true;
    }
}
