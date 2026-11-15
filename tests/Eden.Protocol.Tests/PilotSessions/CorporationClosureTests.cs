using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationClosureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeClosureMovesMembersRetainsAssetsAndAllowsAnotherCorporation(bool liveWorld)
    {
        using TestWorld world = new(liveWorld: liveWorld);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var corporations = world.Services.GetRequiredService<IPlayerCorporations>();
        int originalCeo = corporations.Employment(90000001).Last().CorporationId;
        int originalMember = corporations.Employment(90000002).Last().CorporationId;
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        wallet.Transfer(90000001, new(false, 1000, corporation, 1000, 10000000, "Office funding"));
        var storage = world.Services.GetRequiredService<ICorporationStorage>();
        var office = storage.Rent(90000001, storage.Quote(90000001));
        var item = world
            .Services.GetRequiredService<IPilotInventory>()
            .List(90000001, office.StationId)
            .Single(row => row.TypeId == InventoryDefinitions.CivilianMinerType);
        storage.Move(90000001, office.Id, new([item.Id], office.StationId, CorporationStorageRules.FirstFlag, null));
        long retainedBalance = wallet.Balance(90000001, 1000);
        int vacant = storage.EmptyOffices(90000001);
        long personalBalance = world.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001);
        world.Store.SetPosition(1000000000001, new(1, 2, 3));
        Assert.Throws<InvalidDataException>(() => world.Services.GetRequiredService<ICorporationLeadership>().Close(90000001));
        world.Store.SetPosition(1000000000001, null);
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            Assert.Null(ceo.Reply("ResignFromCEO", [null]));
            member.Advance();
            Assert.Equal(originalCeo, ceo.Context.Pilot.CorpId);
            Assert.Equal(originalMember, member.Context.Pilot.CorpId);
            Assert.False(ceo.Context.Pilot.Identity.CorporationAccess!.Roles.HasAuthority);
            Assert.True(corporations.Read(corporation)!.ClosedAt.HasValue);
            Assert.Equal(0, corporations.Read(corporation)!.MemberCount);
            var reply = Assert.IsType<PilotCallResult.Success>(ceo.CallService("corpmgr", "GetPublicInfo", [(long)corporation]));
            var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply))).State);
            Assert.Equal(true, fields["deleted"]);
            Assert.Equal(90000001L, fields["ceoID"]);
            Assert.Equal(vacant + 1, storage.EmptyOffices(90000001));
            Assert.DoesNotContain(corporation, storage.Tenants(90000001));
            Assert.Throws<InvalidDataException>(() => storage.List(90000001, office.Id, null));
            Assert.Throws<InvalidDataException>(() =>
                world.Services.GetRequiredService<ICorporationMembership>().Apply(90000002, corporation, "Rejoin")
            );
            Assert.IsType<PilotCallResult.Failure>(ceo.Call("ResignFromCEO", [null]));
        }
        Assert.Equal(personalBalance, world.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001));
        Assert.Throws<InvalidDataException>(() => corporations.Create(90000001, corporations.Read(corporation)!.Details));
        int next = corporations
            .Create(90000001, PlayerCorporationTests.Details(corporations.Read(corporation)!.Details.Name, "NEXT"))
            .Corporation.Id;
        Assert.NotEqual(corporation, next);
        world.Services.Dispose();
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: liveWorld);
        Assert.NotNull(reopened.GetRequiredService<IPlayerCorporations>().Read(corporation)!.ClosedAt);
        Assert.Equal(next, reopened.GetRequiredService<IPlayerCorporations>().Identity(90000001).Id);
        using WorldDbContext saved = new(world.Path);
        Assert.Equal(corporation, saved.Set<OwnedItem>().Find(item.Id)!.OwnerId);
        Assert.Equal(office.Id, saved.Set<OwnedItem>().Find(item.Id)!.LocationId);
        Assert.Equal(retainedBalance, saved.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
        Assert.Equal(2, saved.Set<SavedCorporation>().Count());
    }

    [Fact]
    public void FailedClosureRollsBackAllMembersAndHistoricalChanges()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        using (WorldActionScope transaction = new(new(world.Path), TransactionParticipants.None))
        {
            transaction.World.RemoveRange(
                transaction.World.Set<SavedCorporationEmployment>().Where(row => row.PilotId == 90000002).ToArray()
            );
            transaction.Commit();
        }
        long before = membership.Head(90000001);
        Assert.Throws<InvalidDataException>(() => world.Services.GetRequiredService<ICorporationLeadership>().Close(90000001));
        Assert.Equal(before, membership.Head(90000001));
        var corporations = world.Services.GetRequiredService<IPlayerCorporations>();
        Assert.Null(corporations.Read(corporation)!.ClosedAt);
        Assert.Equal(2, corporations.Read(corporation)!.MemberCount);
        Assert.Equal(corporation, corporations.Identity(90000001).Id);
        Assert.Equal(corporation, corporations.Identity(90000002).Id);
    }

    [Fact]
    public void ClosingRejectsMembersAndEligibleSuccessorsAndExpiresApplications()
    {
        using TestWorld world = new(liveWorld: true);
        PlayerCorporationTests.Prepare(world, 90000002);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var leadership = world.Services.GetRequiredService<ICorporationLeadership>();
        Assert.Throws<InvalidDataException>(() => leadership.Close(90000002));
        Assert.Throws<InvalidDataException>(() => leadership.Close(90000001));
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        membership.Remove(90000002, 90000002);
        var application = membership.Apply(90000002, corporation, "Waiting");
        long fee = CorporationRecruitmentRules.Fee(1, true);
        world.Services.GetRequiredService<ICorporationWallet>().Transfer(90000001, new(false, 1000, corporation, 1000, fee, "Advertising"));
        var recruitment = world.Services.GetRequiredService<ICorporationRecruitment>();
        recruitment.Create(90000001, new("Before closure", "", 0, 0, 0, 0, 0, [90000001]), 1);
        Assert.Single(recruitment.ForCorporation(corporation));
        leadership.Close(90000001);
        Assert.Empty(recruitment.ForCorporation(corporation));
        Assert.Equal(
            CorporationApplicationStatus.RejectedByCorporation,
            Assert.Single(membership.Applications(90000002, false), row => row.Id == application.Id).Status
        );
        Assert.Throws<InvalidDataException>(() =>
            membership.Update(
                90000002,
                new(application.Id, 90000002, corporation, "", CorporationApplicationStatus.Joined, "", application.AppliedAt)
            )
        );
    }

    [Fact]
    public void Version161MigrationPreservesOpenCorporationsAndSupportsRepeatedClosures()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        world.Services.Dispose();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw(
                """
                DROP INDEX IX_WorldCorporation_CeoId;
                DROP INDEX IX_WorldCorporation_Name;
                ALTER TABLE WorldCorporation DROP COLUMN ClosedAt;
                CREATE UNIQUE INDEX IX_WorldCorporation_CeoId ON WorldCorporation(CeoId);
                CREATE UNIQUE INDEX IX_WorldCorporation_Name ON WorldCorporation(Name);
                PRAGMA user_version=161;
                """
            );
        }
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        var corporations = reopened.GetRequiredService<IPlayerCorporations>();
        Assert.Null(corporations.Read(corporation)!.ClosedAt);
        var leadership = reopened.GetRequiredService<ICorporationLeadership>();
        leadership.Close(90000001);
        int next = corporations.Create(90000001, PlayerCorporationTests.Details("Next Corporation", "NEXT")).Corporation.Id;
        leadership.Close(90000001);
        Assert.NotNull(corporations.Read(next)!.ClosedAt);
        using WorldDbContext check = new(world.Path);
        Assert.Equal(2, check.Set<SavedCorporation>().Count(row => row.ClosedAt != null));
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }
}
