using Eden.Data.World.Characters;
using Eden.Data.World.Contracts;
using Eden.Gameplay.Contracts;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ContractSearchPerformanceTests
{
    private const long PilotId = 90000001;

    [Theory]
    [InlineData(false, 2000, 4)]
    [InlineData(true, 2000, 3)]
    [InlineData(true, 100, 3)]
    [InlineData(true, 0, 1)]
    public void SearchHydratesOnlyThePageWithConstantSql(bool liveWorld, int count, int commands)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: liveWorld);
        Seed(db, count, clock.Now);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        const int Offset = 50;
        ContractSearch request = new(Offset: Offset, Type: (int)ContractType.All);
        contracts.Search(PilotId, request);
        using var metrics = SessionPerformance.Begin("contract page", _ => { }, force: true)!;
        var found = contracts.Search(PilotId, request);
        Assert.Equal(Math.Min(count, ContractSearchRules.MaximumResults), found.Found);
        Assert.Equal(
            Enumerable.Range(1, count).Reverse().Skip(Offset).Take(ContractSearchRules.PageSize).Select(id => (long)id),
            found.Contracts.Select(c => c.Contract.Id)
        );
        foreach (var details in found.Contracts)
        {
            Assert.Equal(details.Contract.Id * 10, Assert.Single(details.Items).ItemId);
            Assert.NotNull(details.Bids);
            Assert.Equal(new long[] { 200, 100 }, details.Bids.Select(bid => bid.AmountCents));
        }
        Assert.Equal(commands, metrics.Commands);
        Assert.Equal(1, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void EverySortRetainsGameplayKeysAndAscendingIdTiesInBothDirections()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var rows = Seed(db, 32, clock.Now);
        var candidates = rows.Select(row => new ContractDetails(row.Snapshot(), [], [])).ToArray();
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        foreach (var sort in Enum.GetValues<ContractSort>())
        {
            foreach (bool descending in new[] { false, true })
            {
                var expected = (
                    descending
                        ? candidates.OrderByDescending(c => ContractSearchRules.SortKey(c, sort))
                        : candidates.OrderBy(c => ContractSearchRules.SortKey(c, sort))
                ).ThenBy(c => c.Contract.Id);
                var found = contracts.Search(PilotId, new(Type: (int)ContractType.All, Sort: sort, Descending: descending));
                Assert.Equal(rows.Count, found.Found);
                Assert.Equal(expected.Select(c => c.Contract.Id), found.Contracts.Select(c => c.Contract.Id));
            }
        }
    }

    [Fact]
    public void ManagedHeaderFiltersApplyBeforeTheResultCapAndOffset()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        Seed(db, 1100, clock.Now);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<SavedContract>().Find(1L)!.Title = "Étoile";
            world.Set<SavedContract>().Find(2L)!.Description = "An ÉTOILE contract";
            world.SaveChanges();
        }
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        var found = contracts.Search(PilotId, new(Type: (int)ContractType.All, Description: "étoile", Offset: 1));
        Assert.Equal(2, found.Found);
        Assert.Equal(1, Assert.Single(found.Contracts).Contract.Id);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data)
                VALUES ('client/systems','30000142','{{"securityStatus":1.0}}');
                """
            );
        }
        var safe = contracts.Search(PilotId, new(Type: (int)ContractType.All, SecurityClasses: [(int)ContractSecurityClass.Safe]));
        Assert.Equal(1100 / 3, safe.Found);
        Assert.All(safe.Contracts, c => Assert.Equal(30000142, c.Contract.SystemId));
        Assert.Empty(contracts.Search(PilotId, new(SecurityClasses: [])).Contracts);
    }

    [Fact]
    public void ItemFiltersMatchOneOfferedItemAndCountOnlyTopLevelItems()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        Seed(db, 3, clock.Now);
        using (WorldDbContext world = new(db.Path))
        {
            foreach (var row in world.Set<SavedContract>())
            {
                row.Type = ContractType.ItemExchange;
            }
            world.Add(
                new SavedContractItem
                {
                    ContractId = 1,
                    ItemId = 11,
                    TypeId = 601,
                    CategoryId = 6,
                    GroupId = 25,
                    Offered = true,
                }
            );
            world.Add(
                new SavedContractItem
                {
                    ContractId = 2,
                    ItemId = 21,
                    TypeId = 601,
                    CategoryId = 6,
                    GroupId = 25,
                    Offered = true,
                    ParentId = 20,
                }
            );
            world.Add(
                new SavedContractItem
                {
                    ContractId = 3,
                    ItemId = 31,
                    TypeId = 601,
                    CategoryId = 6,
                    GroupId = 25,
                    Offered = false,
                }
            );
            world.SaveChanges();
        }
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        Assert.Empty(contracts.Search(PilotId, new(ItemTypes: [601], CategoryId: 7)).Contracts);
        Assert.Equal(new long[] { 2, 1 }, contracts.Search(PilotId, new(ItemTypes: [601])).Contracts.Select(c => c.Contract.Id));
        Assert.Equal(new long[] { 3, 2 }, contracts.Search(PilotId, new(ExcludeMultiple: true)).Contracts.Select(c => c.Contract.Id));
        Assert.Equal(new long[] { 2, 1 }, contracts.Search(PilotId, new(ExcludeTrade: true)).Contracts.Select(c => c.Contract.Id));
        Assert.Equal(2, Assert.Single(contracts.Search(PilotId, new(ExcludeTrade: true, ExcludeMultiple: true)).Contracts).Contract.Id);
        Assert.Empty(contracts.Search(PilotId, new(ItemTypes: [])).Contracts);
    }

    [Fact]
    public void SearchDoesNotProjectUnrelatedPendingResidentChanges()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        Seed(db, 1, clock.Now);
        var live = db.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Set<Pilot>().Find(PilotId)!.Name = "Pending checkpoint name";
            action.Commit();
        }
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("contract read projection boundary", _ => { }, force: true, command: commands.Add)!)
        {
            Assert.Single(contracts.Search(PilotId, new(Type: (int)ContractType.All)).Contracts);
            Assert.True(metrics.Commands == 3, string.Join(Environment.NewLine, commands));
            Assert.Equal(1, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using WorldDbContext relational = new(db.Path) { IsResidentProjection = true };
        Assert.NotEqual("Pending checkpoint name", relational.Pilots.Find(PilotId)!.Name);
        Assert.Equal("Pending checkpoint name", live.OwnerDirectory.Snapshot.Pilots[PilotId].Identity.Name);
    }

    private static IReadOnlyList<SavedContract> Seed(TestWorld db, int count, long now)
    {
        using WorldDbContext world = new(db.Path);
        List<SavedContract> rows = [];
        for (int id = 1; id <= count; id++)
        {
            SavedContract row = new()
            {
                Id = id,
                IssuerId = PilotId,
                IssuerCorporationId = 1000044,
                Type =
                    id % 3 == 0 ? ContractType.Courier
                    : id % 2 == 0 ? ContractType.Auction
                    : ContractType.ItemExchange,
                Status = ContractStatus.Outstanding,
                StationId = 60003760 + id % 3,
                SystemId = 30000142 + id % 3,
                RegionId = 10000002 + id % 3,
                ConstellationId = 20000020 + id % 3,
                Issued = now,
                Expires = now + TimeSpan.FromDays(1 + id % 3).Ticks,
                PriceCents = id % 5 * 100,
                RewardCents = id % 4 * 100,
                CurrentBidCents = id % 2 * 200,
                BuyoutCents = id % 5 * 500,
                CollateralCents = id % 3 * 200,
                Volume = id % 5 + id * 0.000000000000001,
            };
            rows.Add(row);
            world.Add(row);
            world.Add(
                new SavedContractItem
                {
                    ContractId = id,
                    ItemId = id * 10,
                    TypeId = 3651,
                    Quantity = 1,
                    Offered = true,
                    GroupId = 54,
                    CategoryId = 7,
                }
            );
            world.Add(
                new SavedContractBid
                {
                    ContractId = id,
                    BidderId = 90000002,
                    AmountCents = 100,
                    Time = now,
                }
            );
            world.Add(
                new SavedContractBid
                {
                    ContractId = id,
                    BidderId = 90000002,
                    AmountCents = 200,
                    Time = now + 1,
                }
            );
        }
        world.SaveChanges();
        return rows;
    }
}
