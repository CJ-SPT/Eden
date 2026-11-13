using Eden.Data.Catalog;
using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Market;

internal sealed partial class PilotMarketStore
{
    private readonly record struct InstitutionalListing(long Id, int TypeId, bool Bid, long PriceCents, int Remaining, long Issued);

    private void SeedInstitutional(WorldAction world, CatalogDbContext catalog, CatalogStationDefinition station)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("market.institutional");
        int stock = world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.InstitutionalStock;
        NpcSupplyRules.ValidateStock(stock);
        var preparation = new InstitutionalPreparation(station.Id, catalog.Definitions.Identity.Generation, stock);
        var market = world.Set<NpcMarketOrder>();
        var orders = market.Where(o => o.StationId == station.Id);
        if (world.Prepared(preparation))
        {
            Refresh(world, orders);
            return;
        }
        var existing = orders
            .Where(o => o.Kind == NpcMarketKind.Institutional)
            .Select(o => new InstitutionalListing(o.Id, o.TypeId, o.Bid, o.PriceCents, o.Remaining, o.Issued))
            .ToDictionary(o => (o.TypeId, o.Bid));
        foreach (var offer in InstitutionalOffers(catalog, station))
        {
            if (existing.Remove((offer.TypeId, offer.Bid), out var listing))
            {
                if (listing.Issued != 0 && listing.PriceCents == offer.PriceCents)
                {
                    continue;
                }
                var order = market.Find(listing.Id)!;
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
        foreach (var listing in existing.Values.Where(o => o.Remaining != 0 || o.Issued != 0))
        {
            var retired = market.Find(listing.Id)!;
            retired.Remaining = 0;
            retired.Issued = 0;
        }
        world.Remember(preparation);
        Refresh(world, orders);
    }
}
