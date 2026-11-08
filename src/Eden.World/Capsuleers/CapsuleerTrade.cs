using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable]
internal sealed class CapsuleerTrade(WorldDatabase database, IPilotInventory inventory, IPilotMarket market, IPersonalMarket orders)
{
    internal bool Enabled()
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<SavedCapsuleerHauler>().Any();
    }

    internal void ListStock(long pilot, int station, decimal multiplier = 1)
    {
        var existing = orders.PersonalOrders(pilot);
        if (existing.Count >= PersonalMarketRules.OrderLimit(0, 0, 0, 0))
        {
            return;
        }
        var stock = inventory
            .List(pilot, station)
            .Where(i =>
                i.CategoryId == AsteroidDefinitions.Category
                && i.FlagId == (int)InventoryFlag.Hangar
                && !existing.Any(o => !o.Bid && o.TypeId == i.TypeId && o.StationId == station)
            )
            .OrderBy(i => i.Id)
            .FirstOrDefault();
        if (stock is null)
        {
            return;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var type = catalog.Read(CatalogData.Client.Types, (long)stock.TypeId);
        long price = PersonalMarketRules.Round(
            HaulingRules.OrePrice(type?.BasePrice ?? 0, checked((int)(type?.PortionSize ?? 1))) * multiplier
        );
        if (price <= 0)
        {
            return;
        }
        int quantity = (int)Math.Min(stock.Quantity, PersonalMarketRules.Round(HaulingRules.MaximumLoadCents * multiplier) / price);
        if (quantity <= 0 || market.Balance(pilot) < PersonalMarketRules.BrokerFee(price, quantity, 0.03m))
        {
            return;
        }
        orders.PlaceSell(pilot, [new(stock.Id, station, stock.TypeId, price, quantity)], HaulingRules.OrderDays);
    }
}
