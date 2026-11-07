using Eden.Data.Catalog;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ContractSearchTests
{
    [Fact]
    public void SearchSortingAndOffsetsRemainStableAcrossDifferentIssuers()
    {
        using TestWorld db = new();
        ContractTests.Fund(db, ContractAssetTestData.Pilot);
        ContractTests.Fund(db, ContractAssetTestData.Other);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long cheap = contracts.Create(ContractAssetTestData.Pilot, ContractTests.Draft(ContractAssetTestData.Stack(db), null));
        long expensive = contracts.Create(
            ContractAssetTestData.Other,
            ContractTests.Draft(ContractAssetTestData.Stack(db, owner: ContractAssetTestData.Other), null) with
            {
                PriceCents = 54321,
            }
        );
        Assert.Equal(
            new[] { cheap, expensive },
            contracts
                .Search(ContractAssetTestData.Other, new(Sort: ContractSort.Price, Descending: false))
                .Contracts.Select(c => c.Contract.Id)
        );
        Assert.Equal(
            new[] { expensive, cheap },
            contracts.Search(ContractAssetTestData.Other, new(Sort: ContractSort.Price)).Contracts.Select(c => c.Contract.Id)
        );
        var page = contracts.Search(ContractAssetTestData.Other, new(Sort: ContractSort.Price, Offset: 1));
        Assert.Equal(2, page.Found);
        Assert.Equal(cheap, Assert.Single(page.Contracts).Contract.Id);
    }

    [Fact]
    public void SearchSeparatesPublicAndAssignedContractsAndAppliesCatalogLocationPriceAndTypeFilters()
    {
        using TestWorld db = new();
        long third = Assert.Single(CalendarTestData.AddPilots(db, 1));
        ContractTests.Fund(db, ContractAssetTestData.Pilot);
        ContractTests.Fund(db, ContractAssetTestData.Other);
        long item = ContractAssetTestData.Stack(db);
        long foreign = ContractAssetTestData.Stack(db, owner: ContractAssetTestData.Other);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/systems','30000142','{{"securityStatus":1.0}}');
                """
            );
        }
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long publicId = contracts.Create(ContractAssetTestData.Pilot, ContractTests.Draft(item, null));
        long privateId = contracts.Create(ContractAssetTestData.Other, ContractTests.Draft(foreign, third));
        Assert.Equal(publicId, Assert.Single(contracts.Search(third, new()).Contracts).Contract.Id);
        Assert.Equal(privateId, Assert.Single(contracts.Search(third, new(Availability: 1)).Contracts).Contract.Id);
        Assert.Empty(contracts.Search(ContractAssetTestData.Pilot, new(Availability: 1)).Contracts);
        Assert.Empty(contracts.Search(third, new(Availability: 2)).Contracts);
        Assert.Empty(contracts.Search(third, new(MaxPrice: 100)).Contracts);
        Assert.Single(
            contracts
                .Search(
                    third,
                    new(
                        MinPrice: 12345,
                        MaxPrice: 12345,
                        LocationId: 30000142,
                        IssuerId: ContractAssetTestData.Pilot,
                        ItemTypes: [3651],
                        ItemTypeName: "Civilian",
                        CategoryId: (int)InventoryCategory.Module,
                        SecurityClasses: [(int)ContractSecurityClass.Safe]
                    )
                )
                .Contracts
        );
        Assert.Empty(contracts.Search(third, new(ItemTypes: [601])).Contracts);
        Assert.Empty(contracts.Search(third, new(LocationId: 1)).Contracts);
        Assert.Empty(contracts.Search(third, new(SecurityClasses: [(int)ContractSecurityClass.Low])).Contracts);
        Assert.Empty(contracts.Search(third, new(Description: "does not match")).Contracts);
        Assert.Single(contracts.Search(third, new(Description: "private SALE")).Contracts);
        Assert.Empty(contracts.Search(third, new(Type: (int)ContractType.Courier)).Contracts);
        Assert.Empty(contracts.Search(third, new(Hint: 2)).Contracts);
        Assert.Throws<InvalidDataException>(() => contracts.Search(third, new(MinPrice: 2, MaxPrice: 1)));
        Assert.Throws<InvalidDataException>(() => contracts.Search(third, new(MinVolume: double.NaN)));
        Assert.Throws<InvalidDataException>(() => contracts.Search(third, new(Offset: 1000)));
    }

    [Fact]
    public void OwnerListsPreserveNativeIssuedByIssuedToAndBothSemanticsAndStablePagination()
    {
        using TestWorld db = new();
        ContractTests.Fund(db, ContractAssetTestData.Pilot);
        long item = ContractAssetTestData.Stack(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long first = contracts.Create(ContractAssetTestData.Pilot, ContractTests.Draft(item));
        contracts.Delete(ContractAssetTestData.Pilot, first);
        long second = contracts.Create(ContractAssetTestData.Pilot, ContractTests.Draft(item));
        Assert.Equal(
            new[] { second, first },
            contracts
                .List(ContractAssetTestData.Pilot, new(ContractAssetTestData.Pilot, null, 0, false, 100, null))
                .Select(c => c.Contract.Id)
        );
        Assert.Empty(contracts.List(ContractAssetTestData.Pilot, new(ContractAssetTestData.Pilot, null, 0, true, 100, null)));
        Assert.Equal(
            new[] { second, first },
            contracts
                .List(ContractAssetTestData.Other, new(ContractAssetTestData.Other, null, 0, true, 100, null))
                .Select(c => c.Contract.Id)
        );
        Assert.Equal(
            first,
            Assert
                .Single(contracts.List(ContractAssetTestData.Pilot, new(ContractAssetTestData.Pilot, null, 0, null, 1, second)))
                .Contract.Id
        );
    }
}
