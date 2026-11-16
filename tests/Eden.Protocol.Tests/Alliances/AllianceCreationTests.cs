using Eden.Data.World.Alliances;
using Eden.Data.World.Economy;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Alliances;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Alliances;

public sealed class AllianceCreationTests
{
    internal const long Ceo = 90000001;
    internal const long Other = 90000002;

    internal static AllianceDetails Details(string name = "Eden Alliance", string ticker = "EDNA") =>
        new(name, ticker, "星間 alliance", "https://eden.test");

    internal static int Corporation(TestWorld world, long actor = Ceo, string name = "Alliance Founders", string ticker = "ALF")
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (3363,'Corporation Management',257),(3732,'Empire Control',257);
                INSERT OR REPLACE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (3363,275,1),(3732,275,5);
                """
            );
        }
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            foreach (var skill in new[] { (CorporationRules.ManagementSkill, 1d), (AllianceRules.EmpireControlSkill, 5d) })
            {
                var row =
                    action.Set<PilotSkill>().Find(actor, skill.Item1)
                    ?? action.Add(new PilotSkill { PilotId = actor, TypeId = skill.Item1 }).Entity;
                row.Points = TrainedSkill.PointsForLevel(skill.Item2, 5);
            }
            var wallet = action.Set<PilotWallet>().Find(actor) ?? action.Add(new PilotWallet { PilotId = actor }).Entity;
            wallet.Cents = 3 * AllianceRules.CreationFeeCents;
            action.Commit();
        }
        return world
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(actor, PlayerCorporationTests.Details(name, ticker))
            .Corporation.Id;
    }

    [Fact]
    public async Task NativeCreationChargesOncePublishesPublicIdentityAndReopens()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Corporation(world);
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var wallet = world.Services.GetRequiredService<IPilotWalletReader>();
        long before = wallet.Balance(Ceo);
        using CorporationTestClient client = new(world, 1000001);
        var details = Details();
        int id = checked(
            (int)Assert.IsType<long>(client.Reply("CreateAlliance", [details.Name, details.Ticker, details.Description, details.Url]))
        );
        Assert.Equal(before - AllianceRules.CreationFeeCents, wallet.Balance(Ceo));
        Assert.Equal(id, alliances.Affiliation(corporation));
        Assert.Equal(
            (corporation, Ceo, 1),
            (alliances.Read(id)!.ExecutorCorporationId, alliances.Read(id)!.CreatorId, alliances.Read(id)!.MemberCount)
        );
        Assert.IsType<PilotCallResult.Failure>(client.Call("CreateAlliance", ["Duplicate Alliance", "DUP", "", ""]));
        Assert.Equal(before - AllianceRules.CreationFeeCents, wallet.Balance(Ceo));
        var search = world.Services.GetRequiredService<IUniversalSearch>();
        Assert.Contains((long)id, Assert.Single(search.Search(new("Eden Alliance", [SearchCategory.Alliance]))).Ids);
        Assert.Equal("Eden Alliance", Assert.Single(search.Owners([id])).Name);
        Assert.IsType<PilotCallResult.Failure>(client.CallService("allianceRegistry", "MachoBindObject", [(long)id + 1, null]));
        Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "MachoBindObject", [(long)id, null]));
        var reply = Assert.IsType<AllianceReply>(
            Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "GetAlliance", [(long)id])).Reply
        );
        Assert.NotEmpty(BlueMarshal.Encode(reply));
        var members = Assert.IsType<AllianceMembersReply>(
            Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "GetMembers", [])).Reply
        );
        Assert.Single(Assert.IsType<BlueIndexedRowset>(members.ToBlue()).Rows);
        Assert.NotEmpty(BlueMarshal.Encode(members));
        var ledger = world
            .Services.GetRequiredService<LiveWorld>()
            .Economy.Inspect(action =>
                action.Set<SavedServicePayment>().Where(p => p.PilotId == Ceo && p.Kind == WalletEntryType.AllianceRegistration).ToArray()
            );
        Assert.Equal(-AllianceRules.CreationFeeCents, Assert.Single(ledger).AmountCents);
        var snapshot = alliances.Read(id);
        client.Dispose();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        Assert.Equal(snapshot, reopened.GetRequiredService<IAlliances>().Read(id));
        Assert.Equal(before - AllianceRules.CreationFeeCents, reopened.GetRequiredService<IPilotWalletReader>().Balance(Ceo));
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Theory]
    [InlineData(false, false, 5, 100000000000L)]
    [InlineData(true, true, 5, 100000000000L)]
    [InlineData(true, false, 4, 100000000000L)]
    [InlineData(true, false, 5, 99999999999L)]
    public void FormationRejectionsArePure(bool ceo, bool affiliated, int level, long balance) =>
        Assert.Throws<InvalidDataException>(() => AllianceRules.FormationBalance(ceo, affiliated, level, balance));

    [Fact]
    public async Task ConcurrentFormationHasOneWinnerAndOneCharge()
    {
        using TestWorld world = new(liveWorld: true);
        Corporation(world);
        var alliances = world.Services.GetRequiredService<IAlliances>();
        long before = world.Services.GetRequiredService<IPilotWalletReader>().Balance(Ceo);
        var outcomes = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            alliances.Create(Ceo, Details());
                            return true;
                        }
                        catch (InvalidDataException)
                        {
                            return false;
                        }
                    })
                )
        );
        Assert.Single(outcomes, success => success);
        Assert.Single(alliances.List());
        Assert.Equal(before - AllianceRules.CreationFeeCents, world.Services.GetRequiredService<IPilotWalletReader>().Balance(Ceo));
    }

    [Fact]
    public void DuplicateIdentityAndNonExecutorEditsLeaveStateUnchanged()
    {
        using TestWorld world = new(liveWorld: true);
        Corporation(world);
        Corporation(world, Other, "Other Founders", "OTH");
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var alliance = alliances.Create(Ceo, Details()).Alliance;
        long before = world.Services.GetRequiredService<IPilotWalletReader>().Balance(Other);
        Assert.Throws<InvalidDataException>(() => alliances.Create(Other, Details()));
        Assert.Equal(before, world.Services.GetRequiredService<IPilotWalletReader>().Balance(Other));
        Assert.Null(alliances.Affiliation(world.Services.GetRequiredService<IPlayerCorporations>().Identity(Other).Id));
        Assert.Throws<InvalidDataException>(() => alliances.Update(Other, "Forbidden", ""));
        Assert.Equal(alliance, alliances.Read(alliance.Id));
        alliances.Update(Ceo, "Updated", "https://example.test");
        Assert.Equal("Updated", alliances.Read(alliance.Id)!.Details.Description);
    }
}
