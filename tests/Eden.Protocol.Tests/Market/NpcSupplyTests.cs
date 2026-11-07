using Eden.Data.Catalog;
using Eden.Data.World.Market;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class NpcSupplyTests
{
    [Theory]
    [InlineData(InventoryCategory.Blueprint, true, true, 10L, 1000L)]
    [InlineData(InventoryCategory.Skill, true, true, 10L, 1000L)]
    [InlineData(InventoryCategory.Module, true, true, 10, null)]
    [InlineData(InventoryCategory.Ship, true, true, 10, null)]
    [InlineData(InventoryCategory.Material, true, true, 10, null)]
    [InlineData(InventoryCategory.Blueprint, false, true, 10, null)]
    [InlineData(InventoryCategory.Blueprint, true, false, 10, null)]
    [InlineData(InventoryCategory.Blueprint, true, true, 0, null)]
    public void OnlyPublishedCatalogOfferedBooksAndBlueprintsAreEligible(
        InventoryCategory category,
        bool published,
        bool offered,
        long price,
        long? cents
    )
    {
        Assert.Equal(cents, NpcSupplyRules.Price(new(1, category, published, price, offered)));
        Assert.Null(NpcSupplyRules.Price(new(1, InventoryCategory.Skill, true, 0.001m, true)));
        Assert.Throws<InvalidDataException>(() => NpcSupplyRules.ValidateStock(0));
        Assert.Throws<InvalidDataException>(() => NpcSupplyRules.ValidateStock(NpcSupplyRules.MaximumStock + 1));
    }

    [Fact]
    public void Version87UpgradePreservesDevelopmentStockIdentitiesMoneyAndHistory()
    {
        using TestWorld db = new(emptyWallets: false);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','3651','{{\"published\":1,\"marketGroupID\":1,\"volume\":5}}')"
            );
        }
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{\"services\":[7,14,17]}}')"
            );
        }
        var market = db.Services.GetRequiredService<IPilotMarket>();
        var pilot = db.Store.FindPilot(1000001)!;
        var order = market.Orders(pilot.PilotId).Single(o => o.TypeId == (int)Eden.Gameplay.Mining.MiningLaserType.Civilian && !o.Bid);
        market.Buy(pilot.PilotId, pilot.StationId, [new(order.TypeId, order.PriceCents, 1)]);
        long balance = market.Balance(pilot.PilotId);
        var trade = Assert.Single(market.Transactions(pilot.PilotId));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP INDEX IX_WorldMarketOrder_StationId_TypeId_Bid_Kind;
                ALTER TABLE WorldMarketOrder DROP COLUMN Kind;
                ALTER TABLE WorldEconomy DROP COLUMN InstitutionalStock;
                CREATE UNIQUE INDEX IX_WorldMarketOrder_StationId_TypeId_Bid ON WorldMarketOrder(StationId,TypeId,Bid);
                PRAGMA user_version=87;
                """
            );
        }
        WorldInitialization.Initialize(new(db.Path));
        WorldInitialization.Initialize(new(db.Path));
        Assert.Equal(balance, market.Balance(pilot.PilotId));
        Assert.Equal(trade, Assert.Single(market.Transactions(pilot.PilotId)));
        Assert.Contains(market.Orders(pilot.PilotId), o => o.Id == order.Id && o.PriceCents == order.PriceCents);
        using WorldDbContext saved = new(db.Path);
        Assert.All(saved.Set<NpcMarketOrder>(), o => Assert.Equal(NpcMarketKind.Development, o.Kind));
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }
}
