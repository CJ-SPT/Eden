using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Corporations;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class HistoryQueryTests
{
    private static readonly DateTime September = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime October = September.AddMonths(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeMonthFilteringPrecedesTheLedgerLimitForPersonalAndCorporationWallets(bool liveWorld)
    {
        using TestWorld world = new(liveWorld: liveWorld);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using (WorldDbContext saved = new(world.Path))
        {
            foreach (long owner in new long[] { 90000001, corporation })
            {
                saved.Add(Entry(owner, September.ToFileTimeUtc()));
                for (int i = 0; i < 1000; i++)
                {
                    saved.Add(Entry(owner, October.ToFileTimeUtc()));
                }
            }
            saved.Add(Entry(90000002, September.ToFileTimeUtc()));
            var otherDivision = Entry(corporation, September.ToFileTimeUtc());
            otherDivision.Key = 1001;
            saved.Add(otherDivision);
            saved.SaveChanges();
        }
        using CorporationTestClient client = new(world, 1000001);
        foreach (bool corporate in new[] { false, true })
        {
            var result = client.CallService("account", "GetTransactions", [1000L, 2026L, 9L, corporate]);
            var entries = Assert.IsType<BlueList<WalletJournalEntry>>(Assert.IsType<PilotCallResult.Success>(result).Reply).Items;
            var entry = Assert.Single(entries);
            Assert.Equal(September.ToFileTimeUtc(), entry.Time);
            Assert.Equal("History audit", entry.Description);
        }
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        Assert.Equal(1000, wallet.Journal(90000001, false, 1000).Count);
        Assert.Throws<InvalidDataException>(() => wallet.Journal(90000002, true, 1000, new(0)));
        using WorldDbContext unchanged = new(world.Path);
        Assert.Equal(2004, unchanged.Set<SavedWalletLedger>().Count());
    }

    private static SavedWalletLedger Entry(long owner, long time) =>
        new()
        {
            OwnerId = owner,
            Key = 1000,
            Time = time,
            CounterpartyId = 90000002,
            AmountCents = 1,
            BalanceCents = 1,
            EntryType = (WalletEntryType)CorporationWalletRules.PlayerDonationEntry,
            Reason = "History audit",
        };

    [Fact]
    public void PersonalSourcesAndMarketCursorsUseExclusiveUpperBounds()
    {
        using TestWorld world = new();
        var market = world.Services.GetRequiredService<IPilotMarket>();
        using (WorldDbContext saved = new(world.Path))
        {
            var order = saved
                .Add(
                    new NpcMarketOrder
                    {
                        StationId = 60003760,
                        SystemId = 30000142,
                        RegionId = 10000002,
                        OwnerId = 1000035,
                        TypeId = 34,
                        PriceCents = 1,
                        Entered = 1,
                        Remaining = 1,
                    }
                )
                .Entity;
            saved.SaveChanges();
            foreach (long time in new[] { September.ToFileTimeUtc() - 1, September.ToFileTimeUtc(), October.ToFileTimeUtc() })
            {
                saved.Add(
                    new MarketExecution
                    {
                        PilotId = 90000001,
                        OrderId = order.Id,
                        StationId = order.StationId,
                        CounterpartyId = order.OwnerId,
                        TypeId = order.TypeId,
                        Quantity = 1,
                        PriceCents = 1,
                        BalanceCents = 1,
                        Time = time,
                    }
                );
            }
            saved.SaveChanges();
        }
        var period = new WalletJournalFilter(2026, 9).Period(October);
        var journal = world.Services.GetRequiredService<IPilotWalletReader>().Journal(90000001, period);
        Assert.Equal(September.ToFileTimeUtc(), Assert.Single(journal.Trades).Time);
        var trades = market.Transactions(90000001, new(October.ToFileTimeUtc(), 1));
        Assert.Equal(September.ToFileTimeUtc(), Assert.Single(trades).Time);
        Assert.Throws<InvalidDataException>(() => market.Transactions(90000001, new(Limit: 0)));
        Assert.Null(new WalletJournalFilter(9999, 12).Period(October).Until);
        Assert.Equal(October.AddDays(-30).ToFileTimeUtc(), new WalletJournalFilter(null, null).Period(October).From);
    }

    [Fact]
    public void Version85HistoryIndexesAreUpgradedIdempotentlyAndRowsSurviveReopen()
    {
        using TestWorld world = new();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Add(Entry(90000001, September.ToFileTimeUtc()));
            saved.SaveChanges();
            saved.Database.ExecuteSqlRaw("DROP INDEX IX_WorldWalletLedger_OwnerId_Key_Time; PRAGMA user_version=85");
        }
        WorldInitialization.Initialize(new(world.Path));
        WorldInitialization.Initialize(new(world.Path));
        using WorldDbContext reopened = new(world.Path);
        Assert.Single(reopened.Set<SavedWalletLedger>());
        Assert.Equal(WorldMigration.CurrentVersion, reopened.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Contains(
            "IX_WorldWalletLedger_OwnerId_Key_Time",
            reopened.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='index'").ToArray()
        );
    }
}
