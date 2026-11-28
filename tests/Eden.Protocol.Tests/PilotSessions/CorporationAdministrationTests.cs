using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Corporations;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationAdministrationTests
{
    internal static int CreateAndJoin(TestWorld world)
    {
        PlayerCorporationTests.Prepare(world);
        int corporation = world
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(90000001, PlayerCorporationTests.Details())
            .Corporation.Id;
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        var application = membership.Apply(90000002, corporation, "Application");
        CorporationApplicationUpdate Update(CorporationApplicationStatus status) =>
            new(application.Id, application.PilotId, corporation, application.Text, status, "Welcome", application.AppliedAt);
        membership.Update(90000001, Update(CorporationApplicationStatus.Offered));
        membership.Update(90000002, Update(CorporationApplicationStatus.Joined));
        return corporation;
    }

    [Theory]
    [InlineData(5, 12600)]
    [InlineData(0, 0)]
    public async Task BoundUpdateWithMySkillsRefreshesAndPersistsCapacityForTheActiveCeo(int level, int expected)
    {
        const long ceoId = 90000001;
        const long memberId = 90000002;
        using TestWorld world = new(liveWorld: true);
        int id = CreateAndJoin(world);
        var corporations = world.Services.GetRequiredService<IPlayerCorporations>();
        var live = world.Services.GetRequiredService<LiveWorld>();
        using (CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES
                    (3731,'Megacorp Management',257),(3732,'Empire Control',257),(12241,'Sovereignty',257);
                INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (191,'corporationMemberBonus');
                INSERT OR REPLACE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES
                    (3363,191,20),(3731,191,100),(3732,191,400),(12241,191,2000),
                    (3363,275,1),(3731,275,3),(3732,275,5),(12241,275,8);
                """
            );
        }
        using (var skills = live.Economy.Begin(TransactionParticipants.None))
        {
            foreach (
                var (type, rank) in new (int Type, double Rank)[]
                {
                    (CorporationRules.ManagementSkill, 1),
                    (CorporationRules.MegacorpManagementSkill, 3),
                    (AllianceRules.EmpireControlSkill, 5),
                    (CorporationRules.SovereigntySkill, 8),
                }
            )
            {
                var skill =
                    skills.Set<PilotSkill>().Find(ceoId, type) ?? skills.Add(new PilotSkill { PilotId = ceoId, TypeId = type }).Entity;
                skill.Points = TrainedSkill.PointsForLevel(rank, level);
            }
            skills.Commit();
        }
        world
            .Services.GetRequiredService<ICorporationRoles>()
            .UpdateMembers(ceoId, [new(memberId, null, null, null, new((long)CorporationRole.Director, 0, 0, 0, 0, 0, 0, 0), null, 0)]);
        int original = corporations.Read(id)!.MemberLimit;
        Assert.Equal(CorporationRules.MembersPerManagementLevel, original);

        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient director = new(world, 1000002))
        {
            Assert.IsType<PilotCallResult.Failure>(director.Call("UpdateCorporationAbilities", []));
            Assert.IsType<PilotCallResult.Failure>(ceo.Call("UpdateCorporationAbilities", [true]));
            var result = Assert.IsType<PilotCallResult.Success>(
                ceo.Call("MachoBindObject", [(long)id, new object?[] { "UpdateCorporationAbilities", Array.Empty<object?>(), null }])
            );
            var embedded = Assert.IsType<BoundPilotReply>(result.Reply);
            Assert.Equal((long)expected, BlueMarshal.Decode(BlueMarshal.Encode(embedded.Reply)));
            var refreshed = Assert.IsType<PlayerCorporationReply>(
                Assert.IsType<PilotCallResult.Success>(ceo.Call("GetCorporation", [])).Reply
            );
            Assert.Equal(expected, refreshed.Corporation.MemberLimit);
            Assert.Equal(2, refreshed.Corporation.MemberCount);

            director.Advance();
            var notification = Assert.Single(director.Context.Notifications.OfType<CorporationDetailsChanged>());
            var message = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(notification.Arguments)), 2);
            Assert.Equal(new object?[] { (long)original, (long)expected }, BlueRead.Tuple(BlueRead.Fields(message[1])["memberLimit"], 2));
            director.Context.Notifications.Clear();
            Assert.Equal((long)expected, ceo.Reply("UpdateCorporationAbilities", []));
            director.Advance();
            Assert.DoesNotContain(director.Context.Notifications, notice => notice is CorporationDetailsChanged);
        }
        await live.FlushAsync();
        world.Services.Dispose();
        WorldDatabase database = new(world.Path);
        WorldInitialization.Initialize(database);
        using var reopened = ServerServices.Build(database, liveWorld: true);
        var restored = reopened.GetRequiredService<IPlayerCorporations>().Read(id)!;
        Assert.Equal(expected, restored.MemberLimit);
        Assert.Equal(2, restored.MemberCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeEditsNotifyMembersInOrderAndPersistAcrossReopen(bool liveWorld)
    {
        using TestWorld world = new(liveWorld: liveWorld);
        int id = CreateAndJoin(world);
        var corporations = world.Services.GetRequiredService<IPlayerCorporations>();
        var before = corporations.Read(id)!;
        var money = world.Services.GetRequiredService<IPilotWalletReader>();
        long balance = money.Balance(90000001);
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            Assert.Null(ceo.Reply("UpdateCorporation", ["New 星 description", "https://eden.test", 0.2, false, 0.3]));
            Assert.Null(ceo.Reply("UpdateLogo", [415L, null, 417L, 671L, null, 673L, 0L]));
            object?[] divisions = Enumerable.Range(1, 14).Select(i => (object?)$"Division {i}").ToArray();
            Assert.Null(ceo.Reply("UpdateDivisionNames", divisions));
            var after = corporations.Read(id)!;
            Assert.Equal(before.Details.Name, after.Details.Name);
            Assert.Equal(before.Details.Ticker, after.Details.Ticker);
            Assert.Equal("New 星 description", after.Details.Description);
            Assert.Equal(0.2, after.Details.TaxRate);
            Assert.Equal(0.3, after.Details.LoyaltyPointTaxRate);
            Assert.False(after.Details.ApplicationsEnabled);
            Assert.Equal(new CorporationLogo(415, null, 417, 671, null, 673, 0), after.Details.Logo);
            member.Advance();
            var notifications = member.Context.Notifications.OfType<CorporationDetailsChanged>().ToArray();
            Assert.Equal(3, notifications.Length);
            Assert.Equal(before.Details, notifications[0].Change.Before);
            Assert.Equal(notifications[0].Change.After, notifications[1].Change.Before);
            Assert.Equal(notifications[1].Change.After, notifications[2].Change.Before);
            Assert.Equal(after.Details, notifications[2].Change.After);
            var changed = BlueRead.Fields(notifications[0].Arguments[1]);
            Assert.Equal(new object?[] { 0.1, 0.2 }, changed["taxRate"]);
            Assert.Equal(new object?[] { true, false }, changed["isRecruiting"]);
            Assert.DoesNotContain("shape1", changed.Keys);
            var divisionChange = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(notifications[2].Arguments)), 2);
            Assert.Equal("Division 1", BlueMarshal.Text(BlueRead.Tuple(BlueRead.Fields(divisionChange[1])["division1"], 2)[1]));
            member.Advance();
            Assert.Equal(3, member.Context.Notifications.OfType<CorporationDetailsChanged>().Count());
            var wire = Assert.IsType<BlueConstructor>(new PlayerCorporationReply(after).ToBlue());
            var values = Assert.IsType<object?[]>(wire.Arguments[1]);
            string DecodeText(object? value) => BlueMarshal.Text(BlueMarshal.Decode(BlueMarshal.Encode(value)));
            Assert.Equal("Division 1", DecodeText(values[25]));
            Assert.Equal("Division 9", DecodeText(values[32]));
            Assert.Equal("Division 14", DecodeText(values[37]));
            Assert.Equal(balance, money.Balance(90000001));
            Assert.Single(money.Journal(90000001).CorporationPayments!);
            Assert.Throws<InvalidDataException>(() =>
                world.Services.GetRequiredService<ICorporationMembership>().Apply(90000002, id, "Closed")
            );
        }
        var expected = corporations.Read(id);
        world.Services.Dispose();
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        Assert.Equal(expected, reopened.GetRequiredService<IPlayerCorporations>().Read(id));
        using WorldDbContext saved = new(world.Path);
        Assert.Equal(6, saved.Set<SavedCorporationChange>().Count(c => c.AdministrationJson != null));
    }

    [Fact]
    public void UnauthorizedMalformedAndIdenticalEditsLeaveSettingsWalletsAndNotificationsUnchanged()
    {
        using TestWorld world = new();
        int id = CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        var before = world.Services.GetRequiredService<IPlayerCorporations>().Read(id)!;
        Assert.IsType<PilotCallResult.Failure>(member.Call("UpdateCorporation", ["Forged", "", 0d, true, 0d]));
        foreach (
            object?[] args in new object?[][]
            {
                [],
                ["Description", "", double.NaN, true, 0d],
                ["Description", "", -0.1, true, 0d],
                ["Description", "", 0d, true, 1.1],
                [new string('x', 4001), "", 0d, true, 0d],
                ["Description", "bad\nurl", 0d, true, 0d],
                ["Description", "", 0d, "true", 0d],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(ceo.Call("UpdateCorporation", args));
        }
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("UpdateLogo", [-1L, null, null, null, null, null, null]));
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("UpdateLogo", [1L]));
        Assert.IsType<PilotCallResult.Failure>(
            ceo.Call("UpdateDivisionNames", Enumerable.Repeat<object?>(new string('x', 51), 14).ToArray())
        );
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("UpdateCorporation", ["", "", 0d, true, 0d], new() { ["corporationID"] = id }));
        var details = before.Details;
        Assert.Null(
            ceo.Reply(
                "UpdateCorporation",
                [details.Description, details.Url, details.TaxRate, details.ApplicationsEnabled, details.LoyaltyPointTaxRate]
            )
        );
        Assert.Equal(before, world.Services.GetRequiredService<IPlayerCorporations>().Read(id));
        Assert.Empty(ceo.Context.Notifications);
        Assert.Empty(member.Context.Notifications);
        using WorldDbContext saved = new(world.Path);
        Assert.Empty(saved.Set<SavedCorporationChange>().Where(c => c.AdministrationJson != null));
        Assert.Equal(840020000, saved.Set<PilotWallet>().Find(90000001L)!.Cents);
        ceo.Context.LogOff();
        Assert.Same(PilotCallResult.Unsupported, ceo.Call("UpdateLogo", [null, null, null, null, null, null, null]));
    }

    [Fact]
    public void DivisionNamesUpdateLoadedCorporationRowsWhenSavedOrCleared()
    {
        using TestWorld world = new(liveWorld: true);
        int id = CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        var ceoCache = LoadCorporation(ceo);
        var memberCache = LoadCorporation(member);
        const string hangarName = "建造 Materials";
        const string walletName = "Market Funds";
        object?[] names =
        [
            hangarName,
            "Second",
            "Third",
            "Fourth",
            "Fifth",
            "Sixth",
            "Seventh",
            "Master Wallet",
            walletName,
            "Third Wallet",
            "Fourth Wallet",
            "Fifth Wallet",
            "Sixth Wallet",
            "Seventh Wallet",
        ];
        Assert.IsType<PilotCallResult.Failure>(member.Call("UpdateDivisionNames", names));

        Assert.Null(ceo.Reply("UpdateDivisionNames", names));
        member.Advance();
        ApplyCachedEdit(ceo, ceoCache);
        ApplyCachedEdit(member, memberCache);
        Assert.Equal(hangarName, Text(ceoCache["division1"]));
        Assert.Equal(walletName, Text(ceoCache["walletDivision2"]));
        Assert.Equal(hangarName, Text(memberCache["division1"]));
        Assert.Equal(walletName, Text(memberCache["walletDivision2"]));

        Assert.Null(ceo.Reply("UpdateDivisionNames", new object?[14]));
        member.Advance();
        ApplyCachedEdit(ceo, ceoCache);
        ApplyCachedEdit(member, memberCache);
        Assert.Null(ceoCache["division1"]);
        Assert.Null(ceoCache["walletDivision2"]);
        Assert.Null(memberCache["division1"]);
        Assert.Null(memberCache["walletDivision2"]);

        void ApplyCachedEdit(CorporationTestClient client, Dictionary<string, object?> cache)
        {
            var notice = Assert.Single(client.Context.Notifications.OfType<CorporationDetailsChanged>());
            var delta = BlueRead.Fields(notice.Arguments[1]);
            Assert.Equal(new object?[] { id, id }, BlueRead.Tuple(delta["corporationID"], 2));
            var values = delta.Values.Select(value => BlueRead.Tuple(value, 2)).ToArray();
            // These are the tests the pinned cache uses to distinguish changes from insertion/removal.
            Assert.Contains(values, pair => pair[0] is not null);
            Assert.Contains(values, pair => pair[1] is not null);
            foreach (var (field, value) in delta)
            {
                if (cache.ContainsKey(field))
                {
                    cache[field] = BlueRead.Tuple(value, 2)[1];
                }
            }
            client.Context.Notifications.Clear();
        }

        static Dictionary<string, object?> LoadCorporation(CorporationTestClient client)
        {
            var reply = Assert.IsType<PilotCallResult.Success>(client.Call("GetCorporation", []));
            var row = Assert.IsType<BlueConstructor>(reply.Reply.ToBlue());
            var descriptor = Assert.IsType<BlueConstructor>(row.Arguments[0]);
            var columns = Assert.IsType<object?[]>(descriptor.Arguments[0]);
            var values = Assert.IsType<object?[]>(row.Arguments[1]);
            return columns
                .Select(column => Assert.IsType<string>(Assert.IsType<object?[]>(column)[0]))
                .Zip(values)
                .ToDictionary(pair => pair.First, pair => pair.Second);
        }

        static string Text(object? value) => BlueMarshal.Text(BlueMarshal.Decode(BlueMarshal.Encode(value)));
    }

    [Fact]
    public void Version73UpgradeRetainsCorporationsAndAddsAdministrationEvents()
    {
        using TestWorld world = new();
        int id = CreateAndJoin(world);
        world.Services.Dispose();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw("ALTER TABLE WorldCorporationChange DROP COLUMN AdministrationJson; PRAGMA user_version=73;");
        }
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        reopened.GetRequiredService<ICorporationAdministration>().Update(90000001, new("Migrated", "", 0.1, true, 0));
        Assert.Equal("Migrated", reopened.GetRequiredService<IPlayerCorporations>().Read(id)!.Details.Description);
        using WorldDbContext check = new(world.Path);
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(2, check.Set<SavedCorporationChange>().Count(c => c.AdministrationJson != null));
        Assert.Single(check.Set<SavedCorporationPayment>());
    }
}

internal sealed class CorporationTestClient : IDisposable
{
    private readonly IServiceScope _scope;
    private readonly PilotCallRouter _router;
    private readonly CorporationHandler _handler;
    public PilotCallContext Context { get; }
    public IServiceProvider Services => _scope.ServiceProvider;

    public CorporationTestClient(TestWorld world, long account, long? pilotId = null)
    {
        _scope = world.Services.CreateScope();
        var services = _scope.ServiceProvider;
        var pilot = (pilotId is long id ? world.Store.FindPilot(account, id) : world.Store.FindPilot(account))!;
        Context = new(
            account,
            pilot.Account,
            [pilot],
            services.GetRequiredService<FlightSessionFactory>().Create,
            account,
            SignedSettings.Empty
        );
        Context.Select(pilot);
        _router = services.GetRequiredService<PilotCallRouter>();
        _handler = services.GetRequiredService<CorporationHandler>();
        _handler.Enter(Context);
    }

    public void Advance() => _handler.Advance(Context);

    public PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
        CallService("corpRegistry", method, args, keywords);

    public PilotCallResult CallService(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
        _router.Call(
            Context,
            service,
            method,
            BlueMarshal.Decode(BlueMarshal.Encode(args)),
            _ => { },
            keywords is null ? null : BlueRead.Fields(BlueMarshal.Decode(BlueMarshal.Encode(keywords)))
        );

    public object? Reply(string method, object?[] args) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(Call(method, args)).Reply));

    public void Dispose()
    {
        Context.Dispose();
        _scope.Dispose();
    }
}
