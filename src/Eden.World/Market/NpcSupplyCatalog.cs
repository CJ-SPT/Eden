using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Market;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal sealed partial class PilotMarketStore
{
    private void SeedInstitutional(WorldDbContext world, CatalogDbContext catalog, CatalogStationDefinition station)
    {
        int stock = world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.InstitutionalStock;
        NpcSupplyRules.ValidateStock(stock);
        var corporation = catalog.Read(CatalogData.Client.Npccorporations, (long)station.OwnerId);
        var trades = corporation?.CorporationTrades ?? [];
        var existing = world
            .Set<NpcMarketOrder>()
            .Where(o => o.StationId == station.Id && o.Kind == NpcMarketKind.Institutional)
            .ToDictionary(o => (o.TypeId, o.Bid));
        int[] offered = trades.Where(t => t.Value != 0).Select(t => int.Parse(t.Key, CultureInfo.InvariantCulture)).ToArray();
        var types = offered.Select(catalog.FittingType).OfType<CatalogItemDefinition>().OrderBy(type => type.Id).ToArray();
        var definitions = types.ToDictionary(type => type.Id, type => catalog.Read(CatalogData.Client.Types, (long)type.Id));
        HashSet<(int TypeId, bool Bid)> eligible = [];
        foreach (var type in types)
        {
            int typeId = type.Id;
            var definition = definitions.GetValueOrDefault(typeId);
            bool bid = trades[typeId.ToString(CultureInfo.InvariantCulture)] < 0;
            if (
                !RegionalMarketRules.CanUnlock((InventoryCategory)type.CategoryId, type.GroupId, true, true)
                && type.CategoryId != (int)InventoryCategory.Blueprint
            )
            {
                continue;
            }
            // Invented/T2 blueprints never become NPC supply, including if an
            // inconsistent corporation offer is present in an imported catalog.
            if (
                type.CategoryId == (int)InventoryCategory.Blueprint
                && (
                    catalog.Definitions.Attributes(typeId).GetValueOrDefault(DogmaAttributeId.TechLevel) > 1
                    || catalog
                        .Definitions.ManufacturingRecipe(typeId)
                        ?.Products?.Any(p =>
                            p.TypeID is > 0 and <= int.MaxValue
                            && catalog.Definitions.Attributes((int)p.TypeID.Value).GetValueOrDefault(DogmaAttributeId.TechLevel) > 1
                        ) == true
                )
            )
            {
                continue;
            }
            long? price = MarketSnapshotCatalog.Quotes.TryGetValue(typeId, out var quote)
                ? bid
                    ? quote.BuyCents
                    : quote.SellCents
                : null;
            price = NpcSupplyRules.Price(
                new(
                    typeId,
                    (InventoryCategory)type.CategoryId,
                    definition?.Published == 1,
                    (decimal)(price ?? 0) / MarketDefinitions.CentsPerIsk,
                    definition?.MarketGroupID is > 0
                )
            );
            if (price is null)
            {
                continue;
            }
            eligible.Add((typeId, bid));
            if (existing.TryGetValue((typeId, bid), out var order))
            {
                order.PriceCents = price.Value;
                if (order.Issued == 0)
                {
                    order.Issued = Now;
                    order.Remaining = stock;
                    order.Entered = stock;
                }
                continue;
            }
            world.Add(
                new NpcMarketOrder
                {
                    Kind = NpcMarketKind.Institutional,
                    StationId = station.Id,
                    SystemId = station.SolarSystemId,
                    RegionId = station.RegionId,
                    OwnerId = station.OwnerId,
                    TypeId = typeId,
                    Bid = bid,
                    PriceCents = price.Value,
                    Remaining = stock,
                    Entered = stock,
                    Issued = Now,
                }
            );
        }
        foreach (var retired in existing.Where(pair => !eligible.Contains(pair.Key)).Select(pair => pair.Value))
        {
            retired.Remaining = 0;
            retired.Issued = 0;
        }
        world.SaveChanges();
        Refresh(world, world.Set<NpcMarketOrder>().Where(o => o.StationId == station.Id));
    }
}
