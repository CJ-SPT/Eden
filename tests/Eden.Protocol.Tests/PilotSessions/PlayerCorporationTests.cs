using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.Characters;
using Eden.Protocol.Corporations;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationThenNativeCeoRefreshBindsTitlesWithoutRepeatingFormation(bool embeddedCreation)
    {
        using TestWorld db = new();
        Prepare(db);
        using var scope = db.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        const string previousRegistry = "N=10001:42-2";
        Queue<object?> requests = new([
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            PilotServicesTests.Request("corpRegistry", "MachoBindObject", [1000044L, null]),
            embeddedCreation
                ? PilotServicesTests.Request(
                    "corpRegistry",
                    "MachoBindObject",
                    [1000044L, new object?[] { "AddCorporation", CreateArguments(), new Dictionary<string, object?>() }]
                )
                : PilotServicesTests.Request(null, "AddCorporation", CreateArguments(), previousRegistry),
            PilotServicesTests.Request(
                "corpRegistry",
                "MachoBindObject",
                [
                    (long)CorporationRules.FirstCorporationId,
                    new object?[]
                    {
                        "GetTitles",
                        Array.Empty<object?>(),
                        new Dictionary<string, object?> { ["machoVersion"] = 1L },
                    },
                ]
            ),
            PilotServicesTests.Request(null, "GetEveOwners", [], "N=10001:42-3"),
            PilotServicesTests.Request(null, "GetMember", [90000001L], "N=10001:42-3"),
            PilotServicesTests.Request(null, "GetCorporation", [], "N=10001:42-3"),
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
        var errors = packets
            .Where(p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal))
            .Select(p => BlueRead.Tuple(BlueRead.Tuple(p.State, 14)[4], 3)[2])
            .ToArray();
        Assert.Empty(errors);
        var replies = packets.Where(p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).ToArray();
        Assert.Equal(7, replies.Length);
        object? Decode(BlueInstance packet) =>
            BlueMarshal.Decode(BlueRead.Bytes(BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 1)[0]));
        Assert.Equal(
            BlueMarshal.Encode(
                new BoundPilotReply(
                    new("N=10001:42-3", 3),
                    new CorporationTitlesReply(db.Services.GetRequiredService<ICorporationRoles>().Titles(90000001))
                )
            ),
            BlueRead.Bytes(BlueRead.Tuple(BlueRead.Tuple(replies[3].State, 14)[4], 1)[0])
        );
        var owners = BlueRead.Fields(Assert.IsType<BlueInstance>(Decode(replies[4])).State);
        var owner = BlueRead.Tuple(Assert.Single(Assert.IsType<List<object?>>(owners["lines"])), 4);
        Assert.Equal(90000001L, owner[0]);
        var member = BlueRead.Fields(Assert.IsType<BlueInstance>(Decode(replies[5])).State);
        Assert.Equal(CorporationRules.CeoRoles, member["roles"]);
        Assert.Equal(0L, member["titleMask"]);
        Assert.Equal(CorporationRules.FirstCorporationId, db.Store.FindPilot(1000001)!.CorpId);
        using WorldDbContext world = new(db.Path);
        Assert.Single(world.Set<SavedCorporation>());
        Assert.Single(world.Set<SavedCorporationPayment>());
        Assert.Equal(2, world.Set<SavedCorporationEmployment>().Count(e => e.PilotId == 90000001));
        Assert.Equal(840020000, world.Set<PilotWallet>().Find(90000001L)!.Cents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnassignedTitlesReadAsNativeSlotsAndMalformedMutationsAreRejected(bool playerCorporation)
    {
        using TestWorld db = new();
        if (playerCorporation)
        {
            Prepare(db);
            db.Services.GetRequiredService<IPlayerCorporations>().Create(90000001, Details());
        }
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
        PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, "corpRegistry", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
        Assert.Same(PilotCallResult.Unsupported, Call("GetTitles", []));
        context.Select(pilot);
        for (int read = 0; read < 3; read++)
        {
            Assert.Equal(
                playerCorporation ? CorporationRoleRules.TitleCount : 0,
                Assert.IsAssignableFrom<System.Collections.IDictionary>(Reply(Call("GetTitles", []))).Count
            );
        }
        Assert.IsType<PilotCallResult.Failure>(Call("GetTitles", [1000045L]));
        Assert.IsType<PilotCallResult.Failure>(Call("GetTitles", [], new() { ["corporationID"] = 1000045L }));
        Assert.IsType<PilotCallResult.Failure>(Call("UpdateTitle", [1L, "Director"]));
        Assert.IsType<PilotCallResult.Failure>(Call("UpdateMember", [pilot.PilotId, CorporationRules.DirectorRole]));
        Assert.Equal(pilot, context.Pilot);
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public void NpcAndPlayerCorporationRowsShareDefaultLocalizedDivisionFields()
    {
        var npc = new NpcCorporationReply(
            new(1000044, "Starter corporation", "SAK", 60003760, 30000142, null, 3004181, 0.11, 100000000, -1, "")
        );
        var player = new PlayerCorporationReply(
            new(CorporationRules.FirstCorporationId, Details(), 90000001, 90000001, 60003760, 30000142, 134000000000000000, 20, 1)
        );
        var npcRow = Assert.IsType<BlueConstructor>(npc.ToBlue());
        var playerRow = Assert.IsType<BlueConstructor>(player.ToBlue());
        Assert.Equal(BlueMarshal.Encode(npcRow.Arguments[0]), BlueMarshal.Encode(playerRow.Arguments[0]));
        foreach (var row in new[] { npcRow, playerRow })
        {
            var descriptor = Assert.IsType<BlueConstructor>(row.Arguments[0]);
            var columns = Assert.IsType<object?[]>(descriptor.Arguments[0]).Select(c => BlueRead.Tuple(c, 2)).ToArray();
            var values = Assert.IsType<object?[]>(row.Arguments[1]);
            Assert.Equal(38, columns.Length);
            Assert.Equal(columns.Length, values.Length);
            Assert.Equal(
                new[]
                {
                    "division1",
                    "division2",
                    "division3",
                    "division4",
                    "division5",
                    "division6",
                    "division7",
                    "walletDivision2",
                    "walletDivision3",
                    "walletDivision4",
                    "walletDivision5",
                    "walletDivision6",
                    "walletDivision7",
                },
                columns.Skip(25).Select(c => c[0])
            );
            Assert.All(columns.Skip(25), c => Assert.Equal((long)BlueColumnType.Unicode, c[1]));
            Assert.All(values.Skip(25), Assert.Null);
            Assert.NotEmpty(BlueMarshal.Encode(row));
        }
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
        Assert.Equal(new object?[] { 0L, CorporationRoleRules.KnownRoles }, delta["corprole"]);
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
                DROP TABLE WorldCorporationApplication;
                DROP TABLE WorldCorporationChange;
                DROP TABLE WorldCorporationPayment;
                DROP TABLE WorldCorporationEmployment;
                DROP TABLE WorldCorporation;
                PRAGMA user_version=71;
                """
            );
        }
        WorldInitialization.Initialize(new(db.Path));
        using WorldDbContext migrated = new(db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, migrated.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
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
        Assert.Equal(CorporationRules.CeoRoles, member.Roles);
        Assert.Equal(created.Corporation.Id, member.CorporationId);
        Assert.Equal(created.Corporation.CreatedAt, member.JoinedAt);
        var history = corporations.Employment(90000001);
        Assert.Equal(2, history.Count);
        Assert.Equal(before[0], history[1]);
        var selected = db.Store.FindPilot(1000001)!;
        Assert.Equal(created.Corporation.Id, selected.CorpId);
        Assert.Equal("EDEN", selected.CorporationTicker);
        Assert.Equal(CorporationRules.CeoRoles, selected.Identity.CorporationRoles);
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
        Assert.Equal(38, values.Length);
        Assert.Equal(90000001L, values[6]);
        Assert.Equal(1, values[12]);
        Assert.NotEmpty(BlueMarshal.Encode(own));
        var member = BlueRead.Fields(Assert.IsType<BlueInstance>(Reply(Call("corpRegistry", "GetMember", [90000001L]))).State);
        Assert.Equal(CorporationRules.CeoRoles, member["roles"]);
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
        Assert.Equal(new object?[] { 0L, CorporationRoleRules.KnownRoles }, changes["corprole"]);
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
