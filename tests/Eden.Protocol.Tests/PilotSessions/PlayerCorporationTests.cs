using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.Corporations;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class PlayerCorporationTests
{
    internal static CorporationDetails Details(string name = "Eden Explorers", string ticker = "EDEN") =>
        new(name, ticker, "星間 explorers", "https://example.com", 0.1, 0.05, new(null, null, null, null, null, null, null), true, false);

    internal static void Prepare(TestWorld db, long pilot = 90000001)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (3363,'Corporation Management',257);
                INSERT OR IGNORE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (3363,275,1);
                """
            );
        }
        using WorldDbContext world = new(db.Path);
        world.Database.ExecuteSql($"INSERT OR REPLACE INTO WorldPilotSkill(PilotId,TypeId,Points) VALUES ({pilot},3363,250)");
        world.Database.ExecuteSql($"INSERT OR REPLACE INTO WorldWallet(PilotId,Cents) VALUES ({pilot},1000000000)");
    }

    internal static object?[] CreateArguments(CorporationDetails? value = null)
    {
        var details = value ?? Details();
        return
        [
            details.Name,
            details.Ticker,
            details.Description,
            details.Url,
            details.TaxRate,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            details.ApplicationsEnabled,
            details.FriendlyFireEnabled,
            details.LoyaltyPointTaxRate,
        ];
    }

    [Fact]
    public async Task CreationChangesTheSessionBeforeItsReplyAndInvalidatesTheOldRegistry()
    {
        using TestWorld db = new();
        Prepare(db);
        using var scope = db.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        Queue<object?> requests = new([
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            PilotServicesTests.Request("corpRegistry", "MachoBindObject", [1000044L, null]),
            PilotServicesTests.Request("corpRegistry", "AddCorporation", CreateArguments()),
            PilotServicesTests.Request(null, "GetMember", [90000001L], "N=10001:42-2"),
        ]);
        List<BlueInstance> packets = [];
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
                () => requests.TryDequeue(out var next) ? Task.FromResult(next) : throw new EndOfStreamException(),
                packet =>
                {
                    packets.Add(Assert.IsType<BlueInstance>(packet));
                    return Task.CompletedTask;
                },
                _ => { },
                1000001,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        var changes = packets.Where(p => p.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, changes.Length);
        var delta = BlueRead.Fields(BlueRead.Tuple(BlueRead.Tuple(BlueRead.Tuple(changes[1].State, 14)[4], 3)[1], 2)[1]);
        Assert.Equal(new object?[] { 1000044, CorporationRules.FirstCorporationId }, delta["corpid"]);
        Assert.Equal(new object?[] { 0L, CorporationRules.DirectorRole }, delta["corprole"]);
        Assert.EndsWith(".CallRsp", packets[packets.IndexOf(changes[1]) + 1].Name);
        Assert.Single(packets, p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
    }

    [Fact]
    public void Version71MigratesWithoutChangingExistingMembershipOrWallets()
    {
        using TestWorld db = new();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldCorporationPayment;
                DROP TABLE WorldCorporationEmployment;
                DROP TABLE WorldCorporation;
                PRAGMA user_version=71;
                """
            );
        }
        WorldInitialization.Initialize(new(db.Path));
        using WorldDbContext migrated = new(db.Path);
        Assert.Equal(72, migrated.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.All(migrated.Pilots, p => Assert.Equal(1000044, p.CorporationId));
        Assert.Empty(migrated.Set<PilotWallet>());
        Assert.Empty(migrated.Set<SavedCorporation>());
        Assert.Empty(migrated.Set<SavedCorporationPayment>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreationPersistsIdentityCeoWalletAndEmploymentAcrossReopen(bool liveWorld)
    {
        using TestWorld db = new(liveWorld: liveWorld);
        Prepare(db);
        var corporations = db.Services.GetRequiredService<IPlayerCorporations>();
        var before = corporations.Employment(90000001);
        var created = corporations.Create(90000001, Details());
        Assert.Equal(CorporationRules.FirstCorporationId, created.Corporation.Id);
        Assert.Equal(840020000, created.BalanceCents);
        Assert.Equal(90000001, created.Corporation.CeoId);
        Assert.Equal(1, created.Corporation.MemberCount);
        Assert.Equal(20, created.Corporation.MemberLimit);
        Assert.Equal(60003760, created.Corporation.StationId);
        Assert.Equal(30000142, created.Corporation.SystemId);
        Assert.Equal(Details(), created.Corporation.Details);
        Assert.Equal(created.Corporation, corporations.Read(created.Corporation.Id));
        var member = Assert.Single(corporations.Members(90000001));
        Assert.Equal(CorporationRules.DirectorRole, member.Roles);
        Assert.Equal(created.Corporation.Id, member.CorporationId);
        Assert.Equal(created.Corporation.CreatedAt, member.JoinedAt);
        var history = corporations.Employment(90000001);
        Assert.Equal(2, history.Count);
        Assert.Equal(before[0], history[1]);
        var selected = db.Store.FindPilot(1000001)!;
        Assert.Equal(created.Corporation.Id, selected.CorpId);
        Assert.Equal("EDEN", selected.CorporationTicker);
        Assert.Equal(CorporationRules.DirectorRole, selected.Identity.CorporationRoles);
        Assert.Equal(1000044, db.Store.FindPilot(1000002)!.CorpId);
        Assert.Equal(created.BalanceCents, db.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001));
        Assert.Single(db.Services.GetRequiredService<IPilotWalletReader>().Journal(90000001).CorporationPayments!);
        db.Services.Dispose();
        WorldInitialization.Initialize(new(db.Path));
        using var reopened = ServerServices.Build(new(db.Path));
        var saved = reopened.GetRequiredService<IPlayerCorporations>();
        Assert.Equal(created.Corporation, saved.Read(created.Corporation.Id));
        Assert.Equal(history, saved.Employment(90000001));
        Assert.Equal(selected, reopened.GetRequiredService<SqliteWorld>().FindPilot(1000001));
    }

    [Theory]
    [InlineData("funds")]
    [InlineData("skill")]
    [InlineData("space")]
    [InlineData("name")]
    [InlineData("ticker")]
    [InlineData("tax")]
    public void RejectedCreationLeavesAllStateUnchanged(string reason)
    {
        using TestWorld db = new();
        Prepare(db);
        using (WorldDbContext world = new(db.Path))
        {
            if (reason == "funds")
            {
                world.Set<PilotWallet>().Find(90000001L)!.Cents = CorporationRules.CreationFeeCents - 1;
            }
            if (reason == "skill")
            {
                world.Set<PilotSkill>().Find(90000001L, CorporationRules.ManagementSkill)!.Points = 249;
            }
            world.SaveChanges();
        }
        if (reason == "space")
        {
            db.Store.SetPosition(1000000000001, new(1, 2, 3));
        }
        var details = Details();
        details = reason switch
        {
            "name" => details with { Name = "  " },
            "ticker" => details with { Ticker = "TOOLONG" },
            "tax" => details with { TaxRate = double.NaN },
            _ => details,
        };
        var corporations = db.Services.GetRequiredService<IPlayerCorporations>();
        long balance = db.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001);
        Assert.Throws<InvalidDataException>(() => corporations.Create(90000001, details));
        Assert.Equal(balance, db.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001));
        Assert.Equal(1000044, db.Store.FindPilot(1000001)!.CorpId);
        using WorldDbContext check = new(db.Path);
        Assert.Empty(check.Set<SavedCorporation>());
        Assert.Empty(check.Set<SavedCorporationPayment>());
        Assert.Empty(check.Set<SavedCorporationEmployment>());
    }

    [Fact]
    public void DuplicateNamesTickersAndRepeatedCreationCannotChargeOrMoveEitherPilot()
    {
        using TestWorld db = new();
        Prepare(db);
        Prepare(db, 90000002);
        var corporations = db.Services.GetRequiredService<IPlayerCorporations>();
        var created = corporations.Create(90000001, Details("  Eden Explorers  ", "eden"));
        Assert.Equal("Eden Explorers", created.Corporation.Details.Name);
        Assert.Equal("EDEN", created.Corporation.Details.Ticker);
        Assert.Throws<InvalidDataException>(() => corporations.Create(90000001, Details("Another Corporation", "NEW")));
        Assert.Throws<InvalidDataException>(() => corporations.Create(90000002, Details("eden explorers", "OTHER")));
        Assert.Throws<InvalidDataException>(() => corporations.Create(90000002, Details("Other Corporation", "eden")));
        Assert.Equal(1000000000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(90000002));
        Assert.Equal(created.BalanceCents, db.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001));
        Assert.Equal(1000044, db.Store.FindPilot(1000002)!.CorpId);
        Assert.Empty(corporations.SuggestTickers("Eden"));
        Assert.Equal(new[] { "OTHER" }, corporations.SuggestTickers("Other"));
    }

    [Fact]
    public void NativeCreationRepliesOwnerPrimingMembershipAndJournalMatchSavedState()
    {
        using TestWorld db = new();
        Prepare(db);
        using var scope = db.Services.CreateScope();
        var pilot = db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        PilotCallResult Call(string service, string method, object?[] args) =>
            router.Call(context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { });
        Assert.Same(PilotCallResult.Unsupported, Call("corpRegistry", "AddCorporation", CreateArguments()));
        context.Select(pilot);
        Assert.IsType<PilotCallResult.Failure>(Call("corpRegistry", "MachoBindObject", [1000045L, null]));
        var suggestions = BlueRead.Fields(
            Assert.IsType<BlueInstance>(Reply(Call("corpRegistry", "GetSuggestedTickerNames", ["Eden"]))).State
        );
        Assert.Equal("EDEN", BlueMarshal.Text(BlueRead.Tuple(Assert.Single(Assert.IsType<List<object?>>(suggestions["lines"])), 1)[0]));
        Assert.Null(Reply(Call("corpRegistry", "AddCorporation", CreateArguments())));
        int id = context.Pilot.CorpId;
        Assert.Equal(CorporationRules.FirstCorporationId, id);
        Assert.Equal(8400200d, Assert.Single(context.Notifications.OfType<AccountBalanceChanged>()).Balance);
        var own = Assert.IsType<BlueConstructor>(
            Assert.IsType<PilotCallResult.Success>(Call("corpRegistry", "GetCorporation", [])).Reply.ToBlue()
        );
        var publicRow = Assert.IsType<BlueConstructor>(
            Assert.IsType<PilotCallResult.Success>(Call("corpmgr", "GetCorporations", [(long)id])).Reply.ToBlue()
        );
        Assert.Equal(BlueMarshal.Encode(own), BlueMarshal.Encode(publicRow));
        var values = Assert.IsType<object?[]>(own.Arguments[1]);
        Assert.Equal(25, values.Length);
        Assert.Equal(90000001L, values[6]);
        Assert.Equal(1, values[12]);
        Assert.NotEmpty(BlueMarshal.Encode(own));
        var member = BlueRead.Fields(Assert.IsType<BlueInstance>(Reply(Call("corpRegistry", "GetMember", [90000001L]))).State);
        Assert.Equal(CorporationRules.DirectorRole, member["roles"]);
        Assert.IsType<PilotCallResult.Failure>(Call("corpRegistry", "GetMember", [90000002L]));
        var ownerTable = BlueRead.Tuple(Reply(Call("config", "GetMultiOwnersEx", [new List<object?> { (long)id }])), 2);
        var owner = BlueRead.Tuple(Assert.Single(Assert.IsType<List<object?>>(ownerTable[1])), 5);
        Assert.Equal("Eden Explorers", BlueMarshal.Text(owner[1]));
        Assert.Equal((long)CorporationRules.CorporationType, owner[2]);
        var journal = Assert.IsType<BlueList<WalletJournalEntry>>(
            Assert.IsType<PilotCallResult.Success>(Call("account", "GetTransactions", [1000L, null, null, false])).Reply
        );
        var entry = Assert.Single(journal.Items);
        Assert.Equal(WalletEntryType.CorporationRegistration, entry.Type);
        Assert.Equal(-CorporationRules.CreationFeeCents, entry.AmountCents);
        Assert.Equal(840020000, entry.BalanceCents);
        var packet = PilotSessionWire.SessionChange(1000001, 42, context.Pilot, pilot);
        var changes = BlueRead.Fields(BlueRead.Tuple(BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 3)[1], 2)[1]);
        Assert.Equal(new object?[] { 1000044, id }, changes["corpid"]);
        Assert.Equal(new object?[] { 0L, CorporationRules.DirectorRole }, changes["corprole"]);
        Assert.DoesNotContain("shipid", changes.Keys);
        Assert.Equal(2, db.Services.GetRequiredService<IPlayerCorporations>().Employment(pilot.PilotId).Count);
    }

    [Fact]
    public void MalformedRequestsDoNotSpendMoneyOrChangeMembership()
    {
        using TestWorld db = new();
        Prepare(db);
        using var scope = db.Services.CreateScope();
        var pilot = db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var handler = scope.ServiceProvider.GetRequiredService<CorporationHandler>();
        Assert.IsType<PilotCallResult.Failure>(handler.Call(context, "corpRegistry", "AddCorporation", []));
        var args = CreateArguments();
        args[12] = "true";
        Assert.IsType<PilotCallResult.Failure>(handler.Call(context, "corpRegistry", "AddCorporation", args));
        Assert.IsType<PilotCallResult.Failure>(
            handler.Call(context, "corpRegistry", "AddCorporation", CreateArguments(), new() { ["pilotID"] = 90000002L })
        );
        Assert.Empty(context.Notifications);
        Assert.Equal(pilot, context.Pilot);
        Assert.Equal(1000000000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(pilot.PilotId));
    }

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));
}
