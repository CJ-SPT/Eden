using Eden.Gameplay.Characters;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Gateway;
using Eden.Protocol.Gateway.PlexVault.Log;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed partial class PlexVaultTests
{
    [Fact]
    public async Task GatewayVaultHistoryIncludesEveryAccountPilotAndSurvivesCheckpoint()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(emptyWallets: false, clock: clock, liveWorld: true);
        AddPlex(db);
        TestCatalog.AddSchool(new WorldDatabase(db.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        var live = db.Services.GetRequiredService<LiveWorld>();
        var vault = db.Services.GetRequiredService<IPlexVault>();
        var gateway = db.Services.GetRequiredService<GatewayRequests>();
        var pilot = db.Store.FindPilot(AccountId)!;
        long altId = db
            .Services.GetRequiredService<ICharacterCreation>()
            .Create(AccountId, new("Vault Friend", 1, 1, 0, CharacterRules.UnspecifiedAncestry, [], [], 18));
        var alt = db.Store.ListPilots(AccountId).Single(alt => alt.PilotId == altId);
        var stack = Assert
            .Single(db.Services.GetRequiredService<IPilotMarket>().Buy(pilot.PilotId, pilot.StationId, [new(Plex, 100, 11)]).Changes)
            .Item;
        var earliest = Timestamp.FromDateTimeOffset(clock.GetUtcNow());
        vault.Deposit(pilot.PilotId, pilot.StationId, stack.Id, 10);
        Assert.Throws<PlexVaultRejected>(() => vault.Withdraw(altId, alt.StationId, (int)InventoryFlag.Hangar, 11, forMarket: false));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        vault.Withdraw(altId, alt.StationId, (int)InventoryFlag.Hangar, 3, forMarket: false);
        vault.Withdraw(pilot.PilotId, pilot.StationId, (int)InventoryFlag.Hangar, 2, forMarket: true);

        var history = VaultLog<GetAllLoggedForUserResponse>(gateway, new GetAllLoggedForUserRequest(), AccountId, altId);
        Assert.Equal(3, history.Transactions.Count);
        Assert.StrictEqual(history.Transactions[0].Sequential - 1, history.Transactions[1].Sequential);
        Assert.StrictEqual(history.Transactions[1].Sequential - 1, history.Transactions[2].Sequential);
        var marketId = history.Transactions[0];
        var inventoryId = history.Transactions[1];
        var depositId = history.Transactions[2];
        var deposit = VaultLog<GetLogResponse>(gateway, new GetLogRequest { Identifier = depositId }, AccountId, altId);
        Assert.Equal(1000, deposit.Transaction.AmountTransferred.TotalInCents);
        Assert.Equal(1000, deposit.Transaction.ResultingBalance.TotalInCents);
        Assert.Equal(earliest, deposit.Transaction.Timestamp);
        Assert.Equal((ulong)pilot.PilotId, deposit.InvoiceEntry.Attributes.SourceCharacter.Sequential);
        Assert.True(deposit.InvoiceEntry.Attributes.NoDestination);
        Assert.Equal(1021300u, deposit.InvoiceEntry.Attributes.SummaryMessage.Identifier.Sequential);
        var inventory = VaultLog<GetLogResponse>(gateway, new GetLogRequest { Identifier = inventoryId }, AccountId, pilot.PilotId);
        Assert.Equal(-300, inventory.Transaction.AmountTransferred.TotalInCents);
        Assert.Equal(700, inventory.Transaction.ResultingBalance.TotalInCents);
        Assert.Equal((ulong)altId, inventory.InvoiceEntry.Attributes.DestinationCharacter.Sequential);
        Assert.Equal(
            ("characterID", (ulong)altId),
            (
                Assert.Single(inventory.InvoiceEntry.Attributes.SummaryMessage.Parameters).Key,
                Assert.Single(inventory.InvoiceEntry.Attributes.SummaryMessage.Parameters).Integer
            )
        );
        var market = VaultLog<GetLogResponse>(gateway, new GetLogRequest { Identifier = marketId }, AccountId, altId);
        Assert.Equal(-200, market.Transaction.AmountTransferred.TotalInCents);
        Assert.Equal(500, market.Transaction.ResultingBalance.TotalInCents);
        Assert.Equal(1021780u, market.InvoiceEntry.Attributes.Category.Identifier.Sequential);

        var statistics = VaultLog<GetStatisticsResponse>(gateway, new GetStatisticsRequest(), AccountId, altId);
        Assert.Equal(earliest, statistics.EarliestTransaction);
        Assert.Equal(
            [(1021278u, 1000L, 300L, 2u), (1021780u, 0L, 200L, 1u)],
            statistics.Entries.Select(row =>
                (row.Category.Identifier.Sequential, row.Incomes.TotalInCents, row.Expenses.TotalInCents, row.TransactionsCount)
            )
        );
        Assert.Empty(VaultLog<GetAllLoggedForUserResponse>(gateway, new GetAllLoggedForUserRequest(), OtherAccount, altId).Transactions);
        Assert.Empty(VaultLog<GetStatisticsResponse>(gateway, new GetStatisticsRequest(), OtherAccount, altId).Entries);
        Assert.True(VaultLog<GetLogResponse>(gateway, new GetLogRequest { Identifier = marketId }, OtherAccount, altId).Unavailable);
        Assert.Equal(401u, gateway.Handle(new() { Payload = Any.Pack(new GetAllLoggedForUserRequest()) }).StatusCode);
        Assert.Equal(400u, gateway.Handle(new() { Payload = Any.Pack(new GetLogRequest()) }, AccountId).StatusCode);

        await live.FlushAsync();
        db.Services.Dispose();
        using var reopened = ServerServices.Build(new(db.Path), clock, liveWorld: true);
        var restored = reopened.GetRequiredService<GatewayRequests>();
        Assert.Equal(history, VaultLog<GetAllLoggedForUserResponse>(restored, new GetAllLoggedForUserRequest(), AccountId, altId));
        Assert.Equal(statistics, VaultLog<GetStatisticsResponse>(restored, new GetStatisticsRequest(), AccountId, altId));
        Assert.Equal(inventory, VaultLog<GetLogResponse>(restored, new GetLogRequest { Identifier = inventoryId }, AccountId, altId));
    }

    [Fact]
    public async Task Version236VaultPreservesBalanceAndStartsHistoryWithTheNextTransfer()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(emptyWallets: false, clock: clock, liveWorld: true);
        AddPlex(db);
        var pilot = db.Store.FindPilot(AccountId)!;
        var item = Assert
            .Single(db.Services.GetRequiredService<IPilotMarket>().Buy(pilot.PilotId, pilot.StationId, [new(Plex, 100, 9)]).Changes)
            .Item;
        db.Services.GetRequiredService<IPlexVault>().Deposit(pilot.PilotId, pilot.StationId, item.Id, 9);
        await db.Services.GetRequiredService<LiveWorld>().FlushAsync();
        db.Services.Dispose();
        using (WorldDbContext old = new(db.Path))
        {
            old.Database.ExecuteSqlRaw("DROP TABLE WorldPlexVaultHistory; PRAGMA user_version=236;");
        }
        WorldDatabase database = new(db.Path);
        WorldInitialization.Initialize(database, clock);
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        var vault = reopened.GetRequiredService<IPlexVault>();
        Assert.Equal(9, vault.Balance(AccountId));
        Assert.Empty(vault.History(AccountId));
        vault.Withdraw(pilot.PilotId, pilot.StationId, (int)InventoryFlag.Hangar, 3, forMarket: false);
        var entry = Assert.Single(vault.History(AccountId));
        Assert.Equal((-3, 6L), (entry.Amount, entry.Balance));
    }

    private static T VaultLog<T>(GatewayRequests gateway, IMessage request, long account, long activePilot)
        where T : IMessage, new()
    {
        var reply = gateway.Handle(
            new()
            {
                Payload = Any.Pack(request),
                AuthoritativeContext = new() { Identity = new() { Character = new() { Sequential = checked((uint)activePilot) } } },
            },
            account
        );
        Assert.Equal(200u, reply.StatusCode);
        return reply.Payload.Unpack<T>();
    }
}
