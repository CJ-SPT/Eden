using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationBlueprintVoteTests
{
    private const long Ceo = 90000001;
    private const long Member = 90000002;
    private const int BlueprintType = 1130;

    [Fact]
    public void NativeLockAndUnlockVotesPublishLocksAndRequireOneCeoSanctionAfterRestart()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var prepared = Prepare(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        long lockVote;
        long unlockVote;
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            Assert.Null(
                ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new((long)CorporationRole.HangarCanQuery1)))
            );
            member.Advance();
            member.Context.Notifications.Clear();
            lockVote = Assert.IsType<long>(Reply(ceo, "InsertVoteCase", ["Lock original", "", 5L, Options(prepared), 1L]));
            Assert.Throws<InvalidDataException>(() => voting.Create(Ceo, Draft(prepared)));
            Assert.Throws<InvalidDataException>(() => voting.Create(Ceo, Draft(prepared, CorporationVoteType.UnlockBlueprint)));
            var locked = Assert.IsType<Dictionary<long, object?>>(
                ServiceReply(ceo, "itemLocking", "GetItemsByLocation", [(long)prepared.Office.StationId])
            );
            Assert.Equal(prepared.ItemId, Assert.Single(locked).Key);
            var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(locked[prepared.ItemId]).State);
            Assert.Equal((long)prepared.CorporationId, fields["ownerID"]);
            Assert.Equal((long)prepared.Office.StationId, fields["locationID"]);
            Assert.Equal(
                new HashSet<long> { prepared.Office.StationId },
                Assert.IsType<HashSet<long>>(ServiceReply(ceo, "itemLocking", "GetLockedItemLocations", []))
            );
            member.Advance();
            Assert.True(Assert.Single(member.Context.Notifications.OfType<CorporationBlueprintLockNotification>()).Change.Locked);
            Assert.Throws<InvalidDataException>(() => Withdraw(world, prepared));
            Assert.Null(Reply(ceo, "InsertVote", [(long)prepared.CorporationId, lockVote, 0L]));
            clock.AdvanceWallTime(TimeSpan.FromDays(1));
            voting.Advance();
            Assert.Empty(voting.Actions(Ceo));
            Assert.Throws<InvalidDataException>(() => voting.Activate(Ceo, lockVote));
            Assert.Throws<InvalidDataException>(() => Withdraw(world, prepared));
            unlockVote = Assert.IsType<long>(Reply(ceo, "InsertVoteCase", ["Unlock original", "", 6L, Options(prepared), 1L]));
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        var resumed = reopened.GetRequiredService<ICorporationVoting>();
        Assert.Single(reopened.GetRequiredService<ICorporationBlueprintLocks>().Read(Ceo, prepared.Office.StationId));
        Assert.Throws<InvalidDataException>(() => resumed.Create(Ceo, Draft(prepared, CorporationVoteType.UnlockBlueprint)));
        resumed.Cast(Ceo, prepared.CorporationId, unlockVote, 0);
        Assert.Throws<InvalidDataException>(() => resumed.Activate(Ceo, unlockVote));
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Assert.Equal(unlockVote, Assert.Single(resumed.Actions(Ceo)).Id);
        Assert.Single(reopened.GetRequiredService<ICorporationBlueprintLocks>().Read(Ceo));
        Assert.Throws<InvalidDataException>(() => resumed.Activate(Member, unlockVote));
        resumed.Activate(Ceo, unlockVote);
        Assert.Empty(reopened.GetRequiredService<ICorporationBlueprintLocks>().Read(Ceo));
        Assert.True(resumed.Read(Ceo, prepared.CorporationId, lockVote).Data.HoldReleased);
        Assert.Throws<InvalidDataException>(() => resumed.Activate(Ceo, unlockVote));
        var changes = reopened
            .GetRequiredService<ICorporationStorage>()
            .Move(Ceo, prepared.Office.StationId, new([prepared.ItemId], prepared.Office.Id, (int)InventoryFlag.Hangar, null));
        Assert.Equal(Ceo, Assert.Single(changes).Item.OwnerId);
        Assert.Equal((10, 20), (changes[0].Item.MaterialEfficiency, changes[0].Item.TimeEfficiency));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EmptyDefeatedAndTiedLockVotesReleaseTheImmediateHoldOnce(int mode)
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var prepared = Prepare(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        if (mode == 2)
        {
            world.Services.GetRequiredService<ICorporationShares>().Transfer(Ceo, new(prepared.CorporationId, true, Member, 500));
        }
        long id = voting.Create(Ceo, Draft(prepared));
        Assert.Single(world.Services.GetRequiredService<ICorporationBlueprintLocks>().Read(Ceo));
        if (mode != 0)
        {
            voting.Cast(Ceo, prepared.CorporationId, id, mode == 1 ? 1 : 0);
        }
        if (mode == 2)
        {
            voting.Cast(Member, prepared.CorporationId, id, 1);
        }
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        voting.Advance();
        Assert.Empty(world.Services.GetRequiredService<ICorporationBlueprintLocks>().Read(Ceo));
        Assert.True(voting.Read(Ceo, prepared.CorporationId, id).Data.HoldReleased);
        long head = world.Services.GetRequiredService<ICorporationMembership>().Head(Ceo);
        voting.Advance();
        Assert.Equal(head, world.Services.GetRequiredService<ICorporationMembership>().Head(Ceo));
        Assert.Single(Withdraw(world, prepared));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedOrExpiredUnlockKeepsThePermanentLock(bool expired)
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var prepared = Prepare(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        PassLock(world, clock, prepared);
        long id = voting.Create(Ceo, Draft(prepared, CorporationVoteType.UnlockBlueprint));
        voting.Cast(Ceo, prepared.CorporationId, id, expired ? 0 : 1);
        clock.AdvanceWallTime(TimeSpan.FromDays(expired ? 8 : 1));
        voting.Advance();
        Assert.Throws<InvalidDataException>(() => voting.Activate(Ceo, id));
        Assert.Single(world.Services.GetRequiredService<ICorporationBlueprintLocks>().Read(Ceo));
        Assert.Throws<InvalidDataException>(() => Withdraw(world, prepared));
        Assert.True(voting.Create(Ceo, Draft(prepared, CorporationVoteType.UnlockBlueprint)) > id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void ResidentCommitRejectsLockedIdentityMovementConsumptionAndDeletion(int mutation)
    {
        using TestWorld world = new(liveWorld: true);
        var prepared = Prepare(world);
        world.Services.GetRequiredService<ICorporationVoting>().Create(Ceo, Draft(prepared));
        var resident = world.Services.GetRequiredService<LiveWorld>();
        Assert.Throws<InvalidDataException>(() =>
        {
            using var action = resident.Economy.Begin(TransactionParticipants.None);
            var item = action.Set<OwnedItem>().Find(prepared.ItemId)!;
            switch (mutation)
            {
                case 0:
                    item.OwnerId = Ceo;
                    break;
                case 1:
                    item.LocationId = prepared.Office.StationId;
                    break;
                case 2:
                    item.FlagId = CorporationStorageRules.FirstFlag + 1;
                    break;
                case 3:
                    item.Quantity = 2;
                    break;
                case 4:
                    item.TypeId = InventoryDefinitions.CivilianMinerType;
                    break;
                case 5:
                    item.BlueprintRuns = 1;
                    break;
                default:
                    action.Remove(item);
                    break;
            }
            action.Commit();
        });
        using (var action = resident.Economy.Begin(TransactionParticipants.None))
        {
            var item = action.Set<OwnedItem>().Find(prepared.ItemId)!;
            Assert.Equal(
                ((long)prepared.CorporationId, prepared.Office.Id, BlueprintType, 1, -1),
                (item.OwnerId, item.LocationId, item.TypeId, item.Quantity, item.BlueprintRuns)
            );
            // In-place research metadata may change without moving or consuming the original.
            item.MaterialEfficiency = 11;
            item.TimeEfficiency = 22;
            action.Commit();
        }
    }

    [Fact]
    public void RejectedMixedWithdrawalRollsBackEveryEarlierItemAndDoesNotEmitEvents()
    {
        using TestWorld world = new(liveWorld: true);
        var prepared = Prepare(world);
        long other;
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            other = action.NextItemId();
            action.Add(
                new OwnedItem
                {
                    Id = other,
                    OwnerId = prepared.CorporationId,
                    LocationId = prepared.Office.Id,
                    TypeId = InventoryDefinitions.CivilianMinerType,
                    Quantity = 1,
                    FlagId = CorporationStorageRules.FirstFlag,
                }
            );
            action.Commit();
        }
        world.Services.GetRequiredService<ICorporationVoting>().Create(Ceo, Draft(prepared));
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        long head = membership.Head(Ceo);
        Assert.Throws<InvalidDataException>(() =>
            world
                .Services.GetRequiredService<ICorporationStorage>()
                .Move(Ceo, prepared.Office.StationId, new([other, prepared.ItemId], prepared.Office.Id, (int)InventoryFlag.Hangar, null))
        );
        Assert.Equal(head, membership.Head(Ceo));
        Assert.Equal(
            2,
            world.Services.GetRequiredService<ICorporationStorage>().List(Ceo, prepared.Office.Id, CorporationStorageRules.FirstFlag).Count
        );
    }

    [Fact]
    public void ForgedTargetsCopiesAndUnauthorizedProposalsLeaveNoVotesOrHolds()
    {
        using TestWorld world = new(liveWorld: true);
        var prepared = Prepare(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        Assert.Throws<InvalidDataException>(() => voting.Create(Member, Draft(prepared)));
        Assert.Throws<InvalidDataException>(() => voting.Create(Ceo, Draft(prepared) with { Parameter = 42 }));
        Assert.Throws<InvalidDataException>(() => voting.Create(Ceo, Draft(prepared) with { Parameter1 = BlueprintType + 1 }));
        Assert.Throws<InvalidDataException>(() => voting.Create(Ceo, Draft(prepared) with { Parameter2 = prepared.Office.StationId + 1 }));
        foreach (int variant in Enumerable.Range(0, 5))
        {
            using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
            {
                var item = action.Set<OwnedItem>().Find(prepared.ItemId)!;
                item.BlueprintRuns = variant == 0 ? 10 : -1;
                item.Quantity = variant == 1 ? 2 : 1;
                item.OwnerId = variant == 2 ? Ceo : prepared.CorporationId;
                item.LocationId = variant == 3 ? prepared.Office.StationId : prepared.Office.Id;
                item.FlagId = variant == 4 ? CorporationStorageRules.GoalDeliveriesFlag : CorporationStorageRules.FirstFlag;
                action.Commit();
            }
            Assert.Throws<InvalidDataException>(() => voting.Create(Ceo, Draft(prepared)));
        }
        Assert.Empty(voting.List(Ceo, prepared.CorporationId).Open);
        Assert.Empty(world.Services.GetRequiredService<ICorporationBlueprintLocks>().Read(Ceo));
    }

    [Fact]
    public void BlueprintStateAndQueriesUseNoSqlAndRespectCurrentDivisionAccess()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var prepared = Prepare(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        var locks = world.Services.GetRequiredService<ICorporationBlueprintLocks>();
        using (var measured = SessionPerformance.Begin("resident blueprint vote", _ => { }, force: true)!)
        {
            long id = voting.Create(Ceo, Draft(prepared));
            voting.Cast(Ceo, prepared.CorporationId, id, 0);
            clock.AdvanceWallTime(TimeSpan.FromDays(1));
            voting.Advance();
            Assert.Single(locks.Read(Ceo));
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
        }
        Assert.Empty(locks.Read(Member));
        using CorporationTestClient ceo = new(world, 1000001);
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new((long)CorporationRole.HangarCanQuery1))));
        Assert.Single(locks.Read(Member));
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new())));
        Assert.Empty(locks.Read(Member));
        Assert.IsType<PilotCallResult.Failure>(ceo.CallService("itemLocking", "GetItemsByLocation", []));
        Assert.IsType<PilotCallResult.Failure>(ceo.CallService("itemLocking", "GetItemsByLocation", [-1L]));
        Assert.IsType<PilotCallResult.Failure>(ceo.CallService("itemLocking", "GetLockedItemLocations", [1L]));
        Assert.IsType<PilotCallResult.Failure>(
            ceo.CallService("itemLocking", "GetItemsByLocation", [(long)prepared.Office.StationId], new() { ["force"] = true })
        );
        ceo.Context.LogOff();
        Assert.Same(PilotCallResult.Unsupported, ceo.CallService("itemLocking", "GetItemsByLocation", [(long)prepared.Office.StationId]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingHoldReopensAndSettlesExactlyOnce(bool passes)
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var prepared = Prepare(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        long id = voting.Create(Ceo, Draft(prepared) with { Parameter2 = prepared.Office.Id });
        if (passes)
        {
            voting.Cast(Ceo, prepared.CorporationId, id, 0);
        }
        world.Services.Dispose();
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        using var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        var locks = reopened.GetRequiredService<ICorporationBlueprintLocks>();
        Assert.Equal(passes ? 1 : 0, locks.Read(Ceo, prepared.Office.Id).Count);
        var resumed = reopened.GetRequiredService<ICorporationVoting>();
        Assert.True(resumed.Read(Ceo, prepared.CorporationId, id).Data.Settled);
        long head = reopened.GetRequiredService<ICorporationMembership>().Head(Ceo);
        resumed.Advance();
        Assert.Equal(head, reopened.GetRequiredService<ICorporationMembership>().Head(Ceo));
    }

    [Fact]
    public void Version165UpgradesAndInstallsTheResidentLockProjection()
    {
        using TestWorld world = new(liveWorld: true);
        var prepared = Prepare(world);
        world.Services.Dispose();
        using (WorldDbContext old = new(world.Path))
        {
            old.Database.ExecuteSqlRaw("DROP TABLE WorldCorporationBlueprintLock; PRAGMA user_version=165;");
        }
        WorldInitialization.Initialize(new(world.Path));
        using (var reopened = ServerServices.Build(new(world.Path), liveWorld: true))
        {
            reopened.GetRequiredService<ICorporationVoting>().Create(Ceo, Draft(prepared));
            Assert.Single(reopened.GetRequiredService<ICorporationBlueprintLocks>().Read(Ceo));
        }
        using WorldDbContext saved = new(world.Path);
        Assert.Single(saved.Set<SavedCorporationBlueprintLock>());
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void InstalledOrNonBlueprintItemsCannotAcquireGovernanceLocks()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var prepared = Prepare(world);
        var resident = world.Services.GetRequiredService<LiveWorld>();
        long jobId;
        using (var action = resident.Economy.Begin(TransactionParticipants.None))
        {
            jobId = action.Next("WorldIndustryJob");
            action.Add(
                new IndustryJob
                {
                    Id = jobId,
                    PilotId = Ceo,
                    BlueprintId = prepared.ItemId,
                    BlueprintTypeId = BlueprintType,
                    StationId = prepared.Office.StationId,
                    ProductTypeId = InventoryDefinitions.CivilianMinerType,
                    Runs = 1,
                    OutputQuantity = 1,
                    DurationSeconds = 1,
                    StartTime = clock.Now,
                    EndTime = clock.Now + TimeSpan.TicksPerSecond,
                    Status = IndustryJobStatus.Installed,
                }
            );
            action.Commit();
        }
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        Assert.Throws<InvalidDataException>(() => voting.Create(Ceo, Draft(prepared)));
        using (var action = resident.Economy.Begin(TransactionParticipants.None))
        {
            action.Remove(action.Set<IndustryJob>().Find(jobId)!);
            action.Set<OwnedItem>().Find(prepared.ItemId)!.TypeId = InventoryDefinitions.CivilianMinerType;
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() =>
            voting.Create(Ceo, Draft(prepared) with { Parameter1 = InventoryDefinitions.CivilianMinerType })
        );
        Assert.Empty(voting.List(Ceo, prepared.CorporationId).Open);
    }

    [Fact]
    public void UnlockSanctionCannotTargetAnotherLockGeneration()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var prepared = Prepare(world);
        PassLock(world, clock, prepared);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        long id = voting.Create(Ceo, Draft(prepared, CorporationVoteType.UnlockBlueprint));
        voting.Cast(Ceo, prepared.CorporationId, id, 0);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        voting.Advance();
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            var vote = action.Set<SavedCorporationVote>().Find(id)!;
            vote.DataJson = JsonSerializer.Serialize(vote.Snapshot().Data with { BlueprintLockVoteId = id });
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => voting.Activate(Ceo, id));
        Assert.Single(world.Services.GetRequiredService<ICorporationBlueprintLocks>().Read(Ceo));
        Assert.Null(voting.Read(Ceo, prepared.CorporationId, id).Data.AppliedAt);
    }

    [Fact]
    public async Task UnlockedOriginalCanBeDestroyedAndTheCheckpointReopened()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var prepared = Prepare(world);
        PassLock(world, clock, prepared);
        var resident = world.Services.GetRequiredService<LiveWorld>();
        await resident.FlushAsync();
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        long id = voting.Create(Ceo, Draft(prepared, CorporationVoteType.UnlockBlueprint));
        voting.Cast(Ceo, prepared.CorporationId, id, 0);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        voting.Activate(Ceo, id);
        using (var action = resident.Economy.Begin(TransactionParticipants.None))
        {
            action.Remove(action.Set<OwnedItem>().Find(prepared.ItemId)!);
            action.Commit();
        }
        await resident.FlushAsync();
        Assert.Null(resident.LastSaveFailure);
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        Assert.Empty(reopened.GetRequiredService<ICorporationBlueprintLocks>().Read(Ceo));
        using WorldDbContext saved = new(world.Path);
        Assert.Null(saved.Set<OwnedItem>().Find(prepared.ItemId));
        Assert.Empty(saved.Set<SavedCorporationBlueprintLock>());
    }

    private sealed record Prepared(int CorporationId, CorporationOffice Office, long ItemId);

    private static Prepared Prepare(TestWorld world)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory VALUES (9,'Blueprint');
                INSERT OR IGNORE INTO ItemGroup VALUES (167,'Charge Blueprint',9);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (1130,'Iron Charge S Blueprint',167);
                """
            );
            const string typeMetadata = """{"published":1,"volume":0.01}""";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','1130',{typeMetadata})");
        }
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        world.Services.GetRequiredService<ICorporationWallet>().Transfer(Ceo, new(false, 1000, corporation, 1000, 10000000, "Office"));
        var storage = world.Services.GetRequiredService<ICorporationStorage>();
        var office = storage.Rent(Ceo, storage.Quote(Ceo));
        using var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None);
        long id = action.NextItemId();
        action.Add(
            new OwnedItem
            {
                Id = id,
                OwnerId = corporation,
                TypeId = BlueprintType,
                LocationId = office.Id,
                FlagId = CorporationStorageRules.FirstFlag,
                Quantity = 1,
                MaterialEfficiency = 10,
                TimeEfficiency = 20,
            }
        );
        action.Commit();
        return new(corporation, office, id);
    }

    private static long PassLock(TestWorld world, TestClock clock, Prepared prepared)
    {
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        long id = voting.Create(Ceo, Draft(prepared));
        voting.Cast(Ceo, prepared.CorporationId, id, 0);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        voting.Advance();
        return id;
    }

    private static CorporationVoteDraft Draft(Prepared prepared, CorporationVoteType type = CorporationVoteType.LockBlueprint) =>
        new("Blueprint vote", "", type, 1, ["Yes", "No"], prepared.ItemId, BlueprintType, prepared.Office.StationId);

    private static object?[] Options(Prepared prepared) =>
        [
            new object?[] { "UI/LockBlueprint" },
            new object?[] { "UI/DoNotLockBlueprint" },
            prepared.ItemId,
            (long)BlueprintType,
            (long)prepared.Office.StationId,
        ];

    private static IReadOnlyList<InventoryChange> Withdraw(TestWorld world, Prepared prepared) =>
        world
            .Services.GetRequiredService<ICorporationStorage>()
            .Move(Ceo, prepared.Office.StationId, new([prepared.ItemId], prepared.Office.Id, (int)InventoryFlag.Hangar, null));

    private static object? Reply(CorporationTestClient client, string method, object?[] args) =>
        ServiceReply(client, "voteManager", method, args);

    private static object? ServiceReply(CorporationTestClient client, string service, string method, object?[] args) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(client.CallService(service, method, args)).Reply));
}
