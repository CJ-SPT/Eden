using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Market;
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
        if (corporation?.CorporationTrades is not { } trades)
        {
            return;
        }
        var existing = world
            .Set<NpcMarketOrder>()
            .AsNoTracking()
            .Where(o => o.StationId == station.Id && o.Kind == NpcMarketKind.Institutional)
            .Select(o => o.TypeId)
            .ToHashSet();
        int[] offered = trades.Where(t => t.Value > 0).Select(t => int.Parse(t.Key, CultureInfo.InvariantCulture)).ToArray();
        var types = offered
            .Select(catalog.FittingType)
            .OfType<CatalogItemDefinition>()
            .Where(type => type.CategoryId is (int)InventoryCategory.Blueprint or (int)InventoryCategory.Skill)
            .OrderBy(type => type.Id)
            .ToArray();
        var definitions = types.ToDictionary(type => type.Id, type => catalog.Read(CatalogData.Client.Types, (long)type.Id));
        foreach (var type in types)
        {
            int typeId = type.Id;
            var definition = definitions.GetValueOrDefault(typeId);
            if (
                definition?.BasePrice is not > 0
                || !double.IsFinite(definition.BasePrice.Value)
                || definition.BasePrice.Value > (double)MarketDefinitions.MaximumOrderPrice
            )
            {
                continue;
            }
            long? price = NpcSupplyRules.Price(
                new(typeId, (InventoryCategory)type.CategoryId, definition.Published == 1, (decimal)definition.BasePrice.Value, true)
            );
            if (price is null || existing.Contains(typeId))
            {
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
                    PriceCents = price.Value,
                    Remaining = stock,
                    Entered = stock,
                    Issued = Now,
                }
            );
        }
        world.SaveChanges();
        Refresh(world, world.Set<NpcMarketOrder>().Where(o => o.StationId == station.Id));
    }
}
