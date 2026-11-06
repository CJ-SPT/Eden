using System.Collections;
using Eden.Gameplay.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationFinancialPanelTests
{
    [Fact]
    public void NativeBillsAndMarketTabsReturnAuthorizedSettingsAndEmptyUnissuedHistory()
    {
        using TestWorld world = new();
        int corporation = Prepare(world);
        using CorporationTestClient client = new(world, 1000001);
        var settings = Settings(Reply(client, "billMgr", "GetAutomaticPaySettings", []), corporation);
        Assert.Equal(1000L, settings["divisionID"]);
        Assert.Equal(false, settings[2L]);
        Assert.Equal(false, settings[5L]);
        Assert.Empty(Assert.IsType<List<object?>>(Reply(client, "billMgr", "GetCorporationBills", [])));
        Assert.Empty(Assert.IsType<List<object?>>(Reply(client, "billMgr", "GetCorporationBillsReceivable", [])));
        foreach (object? key in new object?[] { null, 1000L, 1006L })
        {
            Assert.Empty(Assert.IsType<List<object?>>(Reply(client, "marketProxy", "CorpGetTransactions", [null, key])));
        }
        Assert.Empty(world.Services.GetRequiredService<ICorporationWallet>().Journal(90000001, true, 1000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticPaymentSettingsRoundTripAndSurviveDiskOrResidentReopen(bool liveWorld)
    {
        using TestWorld world = new(liveWorld: liveWorld);
        int corporation = Prepare(world);
        using (CorporationTestClient client = new(world, 1000001))
        {
            var result = client.CallService("billMgr", "SendAutomaticPaySettings", [WireSettings(corporation, 1006)]);
            Assert.Null(BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply)));
            var saved = Settings(Reply(client, "billMgr", "GetAutomaticPaySettings", []), corporation);
            Assert.Equal(1006L, saved["divisionID"]);
            Assert.Equal(true, saved[2L]);
            Assert.Equal(false, saved[5L]);
        }
        world.Services.Dispose();
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        var settings = reopened.GetRequiredService<ICorporationBilling>().Read(90000001);
        Assert.Equal(corporation, settings.CorporationId);
        Assert.Equal(1006, settings.DivisionKey);
        Assert.Collection(settings.Rules, rule => Assert.Equal(new(2, true), rule), rule => Assert.Equal(new(5, false), rule));
    }

    [Fact]
    public void FinancialRolesAreCheckedAgainstSavedMembershipAndWritesRequireDivisionWithdrawal()
    {
        using TestWorld world = new();
        int corporation = Prepare(world);
        using CorporationTestClient member = new(world, 1000002);
        Assert.IsType<PilotCallResult.Failure>(member.CallService("billMgr", "GetAutomaticPaySettings", []));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("marketProxy", "CorpGetTransactions", [null, null]));
        Assign(world, CorporationRole.JuniorAccountant);
        Assert.NotNull(Reply(member, "billMgr", "GetAutomaticPaySettings", []));
        Assert.IsType<PilotCallResult.Failure>(
            member.CallService("billMgr", "SendAutomaticPaySettings", [WireSettings(corporation, 1000)])
        );
        Assign(world, CorporationRole.Accountant);
        Assert.IsType<PilotCallResult.Failure>(
            member.CallService("billMgr", "SendAutomaticPaySettings", [WireSettings(corporation, 1001)])
        );
        Assign(world, CorporationRole.Accountant | CorporationRole.AccountCanTake2);
        Assert.IsType<PilotCallResult.Success>(
            member.CallService("billMgr", "SendAutomaticPaySettings", [WireSettings(corporation, 1001)])
        );
        Assign(world, CorporationRole.Trader);
        Assert.Empty(Assert.IsType<List<object?>>(Reply(member, "marketProxy", "CorpGetTransactions", [null, 1001L])));
        Assign(world, CorporationRole.NoRole);
        Assert.IsType<PilotCallResult.Failure>(member.CallService("marketProxy", "CorpGetTransactions", [null, 1001L]));
        Assert.Equal(1001, world.Services.GetRequiredService<ICorporationBilling>().Read(90000001).DivisionKey);
    }

    [Fact]
    public void ForgedAndMalformedSettingsAndMarketFiltersFailWithoutChangingSavedState()
    {
        using TestWorld world = new();
        int corporation = Prepare(world);
        using CorporationTestClient client = new(world, 1000001);
        object[] invalid =
        [
            WireSettings(corporation + 1, 1000),
            WireSettings(corporation, 999),
            new Dictionary<long, object?> { [corporation] = new Dictionary<long, object?> { [99999L] = true } },
            new Dictionary<long, object?> { [corporation] = new Dictionary<long, object?> { [2L] = 1L } },
            new Dictionary<long, object?>(),
        ];
        foreach (var request in invalid)
        {
            Assert.IsType<PilotCallResult.Failure>(client.CallService("billMgr", "SendAutomaticPaySettings", [request]));
        }
        foreach (var request in new object?[][] { [], [null], [null, 999L], [-1L, null], [null, "1000"] })
        {
            Assert.IsType<PilotCallResult.Failure>(client.CallService("marketProxy", "CorpGetTransactions", request));
        }
        Assert.IsType<PilotCallResult.Failure>(client.CallService("billMgr", "GetAutomaticPaySettings", [1L]));
        Assert.IsType<PilotCallResult.Failure>(
            client.CallService("billMgr", "GetAutomaticPaySettings", [], new() { ["otherCorp"] = corporation })
        );
        Assert.IsType<PilotCallResult.Failure>(
            client.CallService("marketProxy", "CorpGetTransactions", [null, null], new() { ["otherCorp"] = corporation })
        );
        var settings = world.Services.GetRequiredService<ICorporationBilling>().Read(90000001);
        Assert.Equal(1000, settings.DivisionKey);
        Assert.All(settings.Rules, rule => Assert.False(rule.Enabled));
        using WorldDbContext saved = new(world.Path);
        Assert.Empty(saved.Set<SavedCorporationBillPaymentSettings>());
    }

    [Fact]
    public void Version78AddsSettingsWithoutChangingWalletOrOwnershipData()
    {
        using TestWorld world = new();
        int corporation = Prepare(world);
        world.Services.Dispose();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw("DROP TABLE WorldCorporationBillPaymentSettings; PRAGMA user_version=78;");
        }
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        Assert.Equal(corporation, reopened.GetRequiredService<ICorporationBilling>().Read(90000001).CorporationId);
        Assert.Equal(0, reopened.GetRequiredService<ICorporationWallet>().Balance(90000001, 1000));
        using WorldDbContext check = new(world.Path);
        Assert.NotNull(check.Set<InventoryOwner>().Find((long)corporation));
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    private static int Prepare(TestWorld world)
    {
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO SourceRecord(Dataset,Key,Data) VALUES
                ('client/billtypes','2','{{"billTypeName":"Rental Bill"}}'),
                ('client/billtypes','5','{{"billTypeName":"Alliance Maintenance"}}');
            """
        );
        return corporation;
    }

    private static Dictionary<long, object?> WireSettings(int corporation, int division) =>
        new() { [corporation] = new BlueDictionary([("divisionID", (long)division), (2L, true), (5L, false)]) };

    private static Dictionary<object, object?> Settings(object? reply, int corporation) =>
        Assert
            .IsType<BlueDictionary>(Assert.IsAssignableFrom<IDictionary>(reply)[(long)corporation])
            .Entries.ToDictionary(e => e.Key is string or byte[] ? (object)BlueMarshal.Text(e.Key) : e.Key!, e => e.Value);

    private static object? Reply(CorporationTestClient client, string service, string method, object?[] args) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(client.CallService(service, method, args)).Reply));

    private static void Assign(TestWorld world, CorporationRole role) =>
        world
            .Services.GetRequiredService<ICorporationRoles>()
            .UpdateMembers(90000001, [new(90000002, null, null, null, new((long)role, 0, 0, 0, 0, 0, 0, 0), null, 0)]);
}
