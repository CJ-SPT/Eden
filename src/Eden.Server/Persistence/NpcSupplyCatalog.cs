using System.Globalization;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal sealed partial class PilotMarketStore
{
    private void SeedInstitutional(WorldDbContext world, CatalogDbContext catalog, Station station)
    {
        int stock = world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.InstitutionalStock;
        NpcSupplyRules.ValidateStock(stock);
        var corporation = catalog
            .FindSource("client/npccorporations", station.OwnerId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientNpccorporations>();
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
        var types = catalog
            .Set<ItemType>()
            .Include(t => t.Group)
            .Where(t =>
                offered.Contains(t.Id)
                && (t.Group.CategoryId == (int)InventoryCategory.Blueprint || t.Group.CategoryId == (int)InventoryCategory.Skill)
            )
            .ToArray();
        var keys = types.Select(t => t.Id.ToString(CultureInfo.InvariantCulture)).ToArray();
        var definitions = catalog
            .Set<SourceRecord>()
            .Where(r => r.Dataset == "client/types" && keys.Contains(r.Key))
            .AsEnumerable()
            .ToDictionary(r => int.Parse(r.Key, CultureInfo.InvariantCulture), r => r.Read<ClientTypes>());
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
                new(typeId, (InventoryCategory)type.Group.CategoryId, definition.Published == 1, (decimal)definition.BasePrice.Value, true)
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
                    RegionId = station.SolarSystem.Constellation.RegionId,
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
