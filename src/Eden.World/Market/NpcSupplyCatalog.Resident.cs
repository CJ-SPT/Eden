using Eden.Data.Catalog;
using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Market;

internal sealed partial class PilotMarketStore
{
    private void SeedInstitutional(EconomyTransaction world, CatalogDbContext catalog, CatalogStationDefinition station)
    {
        int stock = world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.InstitutionalStock;
        NpcSupplyRules.ValidateStock(stock);
        var preparation = new InstitutionalPreparation(station.Id, catalog.Definitions.Identity.Generation, stock);
        var orders = world.Set<NpcMarketOrder>().Where(o => o.StationId == station.Id);
        if (world.Prepared(preparation))
        {
            Refresh(world, orders);
            return;
        }
        var existing = orders.Where(o => o.Kind == NpcMarketKind.Institutional).ToDictionary(o => (o.TypeId, o.Bid));
        HashSet<(int TypeId, bool Bid)> eligible = [];
        foreach (var offer in InstitutionalOffers(catalog, station))
        {
            eligible.Add((offer.TypeId, offer.Bid));
            if (existing.TryGetValue((offer.TypeId, offer.Bid), out var order))
            {
                order.PriceCents = offer.PriceCents;
                if (order.Issued == 0)
                {
                    order.Issued = Now;
                    order.Remaining = stock;
                    order.Entered = stock;
                }
            }
            else
            {
                world.Add(
                    new NpcMarketOrder
                    {
                        Kind = NpcMarketKind.Institutional,
                        StationId = station.Id,
                        SystemId = station.SolarSystemId,
                        RegionId = station.RegionId,
                        OwnerId = station.OwnerId,
                        TypeId = offer.TypeId,
                        Bid = offer.Bid,
                        PriceCents = offer.PriceCents,
                        Remaining = stock,
                        Entered = stock,
                        Issued = Now,
                    }
                );
            }
        }
        foreach (var retired in existing.Where(p => !Enumerable.Contains(eligible, p.Key)).Select(p => p.Value))
        {
            retired.Remaining = 0;
            retired.Issued = 0;
        }
        world.Remember(preparation);
        Refresh(world, world.Set<NpcMarketOrder>().Where(o => o.StationId == station.Id));
    }
}
