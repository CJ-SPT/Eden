using Eden.Gameplay.Inventory;
using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Redemption;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Skills;

public sealed partial class ExpertSystemTests
{
    [Fact]
    public async Task NativeExpertTokenClaimInstallsAndPreservesFailedTokensAcrossRestart()
    {
        const int Tritanium = 34;
        const int InitialQuantity = 5;
        const string UserService = "userSvc";
        TestClock clock = new(s_start);
        using TestWorld world = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(world.Path);
        LoadCatalog(database.CatalogPath, includeLimitSystems: false);
        database.Catalog.PrepareRuntime();
        var live = world.Services.GetRequiredService<LiveWorld>();
        var owner = world.Store.FindPilot(OwnerAccount)!;
        var other = world.Store.FindPilot(OtherAccount)!;
        var redemption = world.Services.GetRequiredService<IAccountRedemption>();
        var inventory = world.Services.GetRequiredService<IPilotInventory>();
        var points = world.Services.GetRequiredService<IPilotFreeSkillPoints>();
        var skills = world.Services.GetRequiredService<IPilotSkillReader>();
        var initialInventory = inventory.List(owner.PilotId, owner.StationId).ToArray();
        var learned = skills.ReadSkills(owner.PilotId).OrderBy(skill => skill.TypeId).ToArray();
        long freePoints = points.Balance(owner.PilotId);
        long installedAt = clock.Now;
        long expiresAt = installedAt + InitialQuantity * Duration;

        var first = redemption.Grant(
            new(
                OwnerAccount,
                "mining-barge-direct",
                MiningBargeOperations,
                InitialQuantity,
                "Mining Barge Operations",
                StationId: owner.StationId
            )
        );
        var topUp = redemption.Grant(new(OwnerAccount, "mining-barge-top-up", MiningBargeOperations, 1, "Mining Barge Operations"));
        var excessive = redemption.Grant(
            new(OwnerAccount, "mining-barge-excessive", MiningBargeOperations, int.MaxValue, "Mining Barge Operations")
        );
        var ordinary = redemption.Grant(new(OwnerAccount, "ordinary-reward", Tritanium, 4, "Tritanium"));
        Assert.True(first.ActivatesExpertSystem);
        Assert.False(ordinary.ActivatesExpertSystem);

        long ordinaryItem;
        using (Client client = new(world.Services, OwnerAccount, select: false))
        using (Client otherClient = new(world.Services, OtherAccount, select: false))
        {
            var pending = Queue(client);
            Assert.Equal(new[] { first.Id, topUp.Id, excessive.Id, ordinary.Id }.Order(), pending.Keys.Order());
            Assert.Equal((long)MiningBargeOperations, pending[first.Id]["typeID"]);
            Assert.Equal((long)InitialQuantity, pending[first.Id]["quantity"]);
            Assert.Equal(installedAt, pending[first.Id]["dateTime"]);
            Assert.Null(pending[first.Id]["stationID"]);
            Assert.Null(pending[first.Id]["massTokenID"]);
            Assert.True(Assert.IsType<bool>(pending[first.Id]["soulbound"]));
            Assert.True(Assert.IsType<bool>(pending[first.Id]["isAutoInject"]));
            Assert.False(Assert.IsType<bool>(pending[ordinary.Id]["soulbound"]));
            Assert.False(Assert.IsType<bool>(pending[ordinary.Id]["isAutoInject"]));
            Assert.Empty(Queue(otherClient));
            Assert.IsType<PilotCallResult.Failure>(Claim(client, other.PilotId, first));
            Assert.IsType<PilotCallResult.Failure>(Claim(otherClient, owner.PilotId, first));
            Assert.Empty(client.Notices());
            Assert.Empty(otherClient.Notices());
            Assert.Equal(4, Queue(client).Count);
            Assert.Empty(world.Services.GetRequiredService<IPilotExpertSystems>().Read(other.PilotId).Systems);

            client.Select();
            Assert.Empty(client.Systems());
            Dictionary<string, object?> direct;
            using (world.Services.GetRequiredService<IPilotOperationScope>().Begin())
            using (GameplaySqlGuard.Begin())
            {
                direct = Result(Claim(client, owner.PilotId, first));
            }
            Assert.Equal(owner.PilotId, direct["charID"]);
            Assert.Null(direct["stationID"]);
            Assert.Null(direct["isk_balance"]);
            Assert.Equal(0L, direct["skillpoints"]);
            Assert.Empty(Rows(direct, "itemsCreated"));
            Assert.Empty(Rows(direct, "errors"));
            AssertSystem(client.Systems(), installedAt, expiresAt);
            Assert.Equal(initialInventory, inventory.List(owner.PilotId, owner.StationId));
            Assert.Equal(freePoints, points.Balance(owner.PilotId));
            Assert.Equal(learned, skills.ReadSkills(owner.PilotId).OrderBy(skill => skill.TypeId));
            var directNotices = client.Notices();
            Assert.Single(directNotices.OfType<RedeemingQueueUpdated>());
            AssertSystemNotice(directNotices, true, installedAt, expiresAt);
            Assert.Single(directNotices.OfType<ServerSkillsChanged>());
            Assert.DoesNotContain(directNotices, notice => notice is ItemChanged || notice.Name == "OnFreeSkillPointsChanged");

            var mixed = Result(Claim(client, owner.PilotId, topUp, ordinary));
            Assert.Contains("30 days", TokenError(mixed, topUp));
            var delivered = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Rows(mixed, "itemsCreated")));
            ordinaryItem = Assert.IsType<long>(delivered["itemID"]);
            Assert.Equal(Tritanium, delivered["typeID"]);
            Assert.Equal(4, delivered["quantity"]);
            Assert.Equal(4, Quantity(inventory, owner, ordinaryItem));
            Assert.Equal((long)owner.StationId, mixed["stationID"]);
            Assert.Null(mixed["isk_balance"]);
            AssertSystem(client.Systems(), installedAt, expiresAt);
            Assert.Equal(new[] { topUp.Id, excessive.Id }.Order(), Queue(client).Keys.Order());
            var mixedNotices = client.Notices();
            Assert.Single(mixedNotices.OfType<RedeemingQueueUpdated>());
            Assert.Equal(ordinaryItem, Assert.Single(mixedNotices.OfType<ItemChanged>()).Item.Id);
            Assert.DoesNotContain(mixedNotices, notice => notice is ExpertSystemsUpdated);
        }

        await live.FlushAsync();
        world.Services.Dispose();
        WorldInitialization.Initialize(database, clock);
        clock.AdvanceWallTime(TimeSpan.FromDays(5));
        using (var offline = ServerServices.Build(database, clock))
        {
            var store = offline.GetRequiredService<IAccountRedemption>();
            var denied = store.Claim(OwnerAccount, owner.PilotId, [new(topUp.Id, topUp.TypeId)], useHomeStation: false);
            var failure = Assert.Single(denied.Errors);
            Assert.Equal(topUp.Id, failure.TokenId);
            Assert.Contains("resident world", failure.Information);
            Assert.Empty(denied.Items);
            Assert.Contains(store.Read(OwnerAccount), token => token.Id == topUp.Id);
        }

        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        using Client restored = new(reopened, OwnerAccount, select: false);
        var restoredInventory = reopened.GetRequiredService<IPilotInventory>();
        Assert.Equal(4, Quantity(restoredInventory, owner, ordinaryItem));
        Assert.Equal(new[] { topUp.Id, excessive.Id }.Order(), Queue(restored).Keys.Order());
        Assert.IsType<PilotCallResult.Failure>(Claim(restored, owner.PilotId, first));
        Assert.Empty(restored.Notices());

        // The original install now has exactly 30 days remaining. The overflowing leg stays pending.
        var extended = Result(Claim(restored, owner.PilotId, excessive, topUp));
        Assert.Contains("date range", TokenError(extended, excessive));
        Assert.Null(extended["isk_balance"]);
        Assert.Equal(0L, extended["skillpoints"]);
        Assert.Empty(Rows(extended, "itemsCreated"));
        Assert.Equal(excessive.Id, Assert.Single(Queue(restored)).Key);
        var preselectionNotices = restored.Notices();
        Assert.Single(preselectionNotices.OfType<RedeemingQueueUpdated>());
        Assert.DoesNotContain(preselectionNotices, notice => notice is ExpertSystemsUpdated or ServerSkillsChanged or ItemChanged);

        restored.Select();
        AssertSystem(restored.Systems(), installedAt, expiresAt + Duration);
        AssertSkill(restored.Skills()[MiningBargeSkill], MiningBargeSkill, null, null, 4d, 4);
        Assert.DoesNotContain(restoredInventory.List(owner.PilotId, owner.StationId), item => item.TypeId == MiningBargeOperations);
        Assert.Equal(freePoints, reopened.GetRequiredService<IPilotFreeSkillPoints>().Balance(owner.PilotId));
        Assert.Equal(learned, reopened.GetRequiredService<IPilotSkillReader>().ReadSkills(owner.PilotId).OrderBy(skill => skill.TypeId));

        static PilotCallResult Claim(Client client, long pilot, params RedeemToken[] selection) =>
            client.Call(
                UserService,
                "ClaimRedeemTokens",
                [selection.Select(token => (object?)new object?[] { token.Id, null }).ToList(), pilot],
                new() { ["useHomeStation"] = false }
            );

        static Dictionary<long, Dictionary<string, object?>> Queue(Client client)
        {
            var reply = Assert.IsType<PilotCallResult.Success>(client.Call(UserService, "GetRedeemTokens", [])).Reply;
            var rows = Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(reply)));
            return rows.Select(row => BlueRead.Fields(Assert.IsType<BlueInstance>(row).State))
                .ToDictionary(row => Assert.IsType<long>(row["tokenID"]));
        }

        static Dictionary<string, object?> Result(PilotCallResult call)
        {
            var success = Assert.IsType<PilotCallResult.Success>(call);
            Assert.NotEmpty(BlueMarshal.Encode(success.Reply));
            return Assert.IsType<Dictionary<string, object?>>(WireAssertions.Materialize(success));
        }

        static List<object?> Rows(Dictionary<string, object?> reply, string key) => Assert.IsType<List<object?>>(reply[key]);

        static string TokenError(Dictionary<string, object?> reply, RedeemToken token)
        {
            var error = Assert.IsType<object?[]>(Assert.Single(Rows(reply, "errors")));
            Assert.Equal(token.Id, error[0]);
            var message = Assert.IsType<BlueConstructor>(error[1]);
            Assert.Equal("exceptions.UserError", message.Name);
            Assert.Equal("CustomInfo", message.Arguments[0]);
            var values = BlueRead.Fields(message.Arguments[1]);
            Assert.Equal(token.TypeId, values["itemTypeID"]);
            return BlueRead.Text(values["info"]);
        }
    }
}
