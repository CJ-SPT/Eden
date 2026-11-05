using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.Corporations;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Persistence;
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
        Assert.Equal(74, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
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

    public CorporationTestClient(TestWorld world, long account)
    {
        _scope = world.Services.CreateScope();
        var services = _scope.ServiceProvider;
        var pilot = world.Store.FindPilot(account)!;
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
        _router.Call(Context, "corpRegistry", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

    public object? Reply(string method, object?[] args) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(Call(method, args)).Reply));

    public void Dispose()
    {
        Context.Dispose();
        _scope.Dispose();
    }
}
