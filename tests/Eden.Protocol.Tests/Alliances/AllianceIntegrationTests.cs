using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.AccessGroups;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Characters;
using Eden.Protocol.Corporations;
using Eden.Protocol.Docking;
using Eden.Protocol.Fittings;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Characters;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;
using static Eden.Protocol.Tests.Alliances.AllianceSocialTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class AllianceIntegrationTests
{
    private static FittingPlan Plan(string name = "Alliance doctrine") =>
        new(name, "Shared template", 601, [new(3651, InventoryFlag.Cargo, 20)]);

    private static BlueInstance NativePlan(long owner) =>
        new(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["ownerID"] = owner,
                ["name"] = "Shared doctrine",
                ["description"] = "Template",
                ["shipTypeID"] = 601L,
                ["fitData"] = new List<object?> { new object?[] { 3651L, 5L, 20L } },
            }
        );

    [Fact]
    public async Task AllianceFittingsAuthorizeCurrentMembersTransferExecutorAndReopen()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        var fittings = world.Services.GetRequiredService<IPilotSavedFittings>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        long saved;
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            saved = Assert.IsType<long>(
                Reply(ceo, "allianceFittingMgr", "SaveFitting", [(long)setup.Alliance, NativePlan(setup.Alliance)])
            );
            Assert.IsType<BlueMap<long, SavedFittingReply>>(
                Assert
                    .IsType<PilotCallResult.Success>(member.CallService("allianceFittingMgr", "GetFittings", [(long)setup.Alliance]))
                    .Reply
            );
            Assert.Single(fittings.Read(Other, setup.Alliance));
            Assert.IsType<PilotCallResult.Failure>(
                member.CallService("allianceFittingMgr", "DeleteFitting", [(long)setup.Alliance, saved])
            );
            Assert.Single(fittings.Read(Ceo, setup.Alliance));
            membership.DeclareSupport(Ceo, setup.Member);
            membership.DeclareSupport(Other, setup.Member);
            Assert.Throws<InvalidDataException>(() => fittings.Rename(Ceo, saved, "Denied", "", setup.Alliance));
            fittings.Rename(Other, saved, "Executor doctrine", "", setup.Alliance);
            Assert.Equal("Executor doctrine", Assert.Single(fittings.Read(Ceo, setup.Alliance)).Plan.Name);
            var imported = fittings.SaveMany(Other, setup.Alliance, [new(-1, Plan()), new(-2, Plan("Second"))]);
            Assert.Throws<InvalidDataException>(() => fittings.DeleteMany(Other, setup.Alliance, [imported[0].FittingId, long.MaxValue]));
            Assert.Equal(3, fittings.Read(Other, setup.Alliance).Count);
            fittings.DeleteMany(Other, setup.Alliance, imported.Select(i => i.FittingId).ToArray());
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var reopenedFits = reopened.GetRequiredService<IPilotSavedFittings>();
        Assert.Equal(saved, Assert.Single(reopenedFits.Read(Other, setup.Alliance)).Id);
        reopened.GetRequiredService<IAllianceMembership>().Remove(Ceo, setup.Founder);
        Assert.Throws<InvalidDataException>(() => reopenedFits.Read(Ceo, setup.Alliance));
        Assert.Throws<InvalidDataException>(() => reopenedFits.Save(Ceo, Plan(), ownerId: setup.Alliance));
        Assert.Single(reopenedFits.Read(Other, setup.Alliance));
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public void AllianceBookmarkAndStructureGrantsRevokeImmediatelyWithSpecificExceptions()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        var groups = world.Services.GetRequiredService<IPilotAccessGroups>();
        var bookmarks = world.Services.GetRequiredService<IPilotBookmarks>();
        var admin = groups.Create(Ceo, "Administrators", "");
        var use = groups.Create(Ceo, "Alliance members", "");
        groups.ChangeMembers(Ceo, use.Id, [new(setup.Alliance, AccessGroupRole.Member)]);
        Assert.Throws<InvalidDataException>(() => groups.ChangeMembers(Ceo, use.Id, [new(setup.Alliance, AccessGroupRole.Admin)]));
        Assert.Throws<InvalidDataException>(() => groups.ChangeMembers(Ceo, use.Id, [new(setup.Alliance, AccessGroupRole.Manager)]));
        var folder = bookmarks.AddSharedFolder(Ceo, "Alliance operations", "", new(admin.Id, null, use.Id, null));
        bookmarks.SetFolderState(Other, folder.Id, true);
        var bookmark = bookmarks.CreateStatic(Ceo, 60003760, folder.Id, null, new("Home", ""), BookmarkExpiry.Never);
        clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        Assert.Equal(bookmark, bookmarks.Find(Other, bookmark.Id));
        groups.ChangeMembers(Ceo, use.Id, [new(setup.Member, AccessGroupRole.Excluded)]);
        Assert.Throws<InvalidDataException>(() => bookmarks.Find(Other, bookmark.Id));
        groups.ChangeMembers(Ceo, use.Id, [new(Other, AccessGroupRole.Member)]);
        bookmarks.SetFolderState(Other, folder.Id, true);
        Assert.Equal(bookmark, bookmarks.Find(Other, bookmark.Id));
        groups.ChangeMembers(Ceo, use.Id, [new(Other, AccessGroupRole.Member), new(setup.Member, AccessGroupRole.Excluded)], remove: true);
        Assert.Equal(bookmark, bookmarks.Find(Other, bookmark.Id));

        const long structureId = 1_900_000_001;
        var resident = world.Services.GetRequiredService<LiveWorld>();
        using (var action = resident.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(
                new SavedStructure
                {
                    Id = structureId,
                    CorporationId = setup.Founder,
                    TypeId = 35832,
                    SystemId = 30000142,
                }
            );
            action.Add(
                new SavedStructureAccess
                {
                    StructureId = structureId,
                    MemberId = setup.Alliance,
                    Allowed = true,
                }
            );
            action.Commit();
        }
        Assert.True(
            resident.Economy.Inspect(action => PilotStructureStore.Access(action, action.Set<SavedStructure>().Find(structureId)!, Other))
        );
        world.Services.GetRequiredService<IAllianceMembership>().Remove(Other, setup.Member);
        Assert.Throws<InvalidDataException>(() => bookmarks.Find(Other, bookmark.Id));
        Assert.False(
            resident.Economy.Inspect(action => PilotStructureStore.Access(action, action.Set<SavedStructure>().Find(structureId)!, Other))
        );
        Assert.Equal(BookmarkAccess.Admin, bookmarks.Folder(Ceo, folder.Id).Access);
    }

    [Fact]
    public void FleetAllianceAdvertsUseFreshMembershipBeforeSessionRefreshAndRecheckInvitations()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        var fleets = world.Services.GetRequiredService<ICooperativeFleets>();
        Guid leaderSession = Guid.NewGuid();
        Guid memberSession = Guid.NewGuid();
        FleetPilot leader = new(Ceo, setup.Founder, 1, 601, 30000142, null);
        FleetPilot member = new(Other, setup.Member, 2, 601, 30000142, null);
        fleets.Update(leaderSession, leader);
        fleets.Update(memberSession, member);
        var fleet = fleets.Create(Ceo);
        FleetAdvertSettings settings = new(
            "Alliance fleet",
            "Operations",
            FleetInviteScope.Alliance,
            null,
            false,
            false,
            new(null, null, [], []),
            new(null, null, [], []),
            false,
            false,
            true,
            null
        );
        fleets.SetAdvert(Ceo, settings);
        Assert.Equal(setup.Alliance, Assert.Single(fleets.AvailableAdverts(Other, 0)).Leader.Pilot.AllianceId);
        fleets.SetAdvert(Ceo, settings with { Scope = FleetInviteScope.Public, Public = new(5, null, [setup.Alliance], []) });
        Assert.Single(fleets.AvailableAdverts(Other, 0));
        fleets.SetAdvert(Ceo, settings with { MemberGroups = new(null, null, [], [setup.Alliance]) });
        Assert.Empty(fleets.AvailableAdverts(Other, 0));
        fleets.SetAdvert(Ceo, settings);
        using (var metrics = SessionPerformance.Begin("alliance fleet adverts", _ => { }, force: true)!)
        {
            for (int cycle = 0; cycle < 10; cycle++)
            {
                Assert.Single(fleets.AvailableAdverts(Other, 0));
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.False(fleets.ApplyToFleet(Other, fleet.Id, 0));
        world.Services.GetRequiredService<IAllianceMembership>().Remove(Other, setup.Member);
        fleets.Update(memberSession, member with { AllianceId = setup.Alliance });
        Assert.Empty(fleets.AvailableAdverts(Other, 0));
        Assert.Throws<InvalidDataException>(() => fleets.Accept(Other, fleet.Id));
        Assert.Null(fleets.Identity(Other));
        fleets.SetAdvert(Ceo, settings with { Scope = FleetInviteScope.Public, Public = new(5, null, [setup.Alliance], []) });
        Assert.Empty(fleets.AvailableAdverts(Other, 0));
        Assert.Throws<InvalidDataException>(() => fleets.SetAdvert(Ceo, settings with { Scope = FleetInviteScope.Militia }));
    }

    [Fact]
    public async Task CombatReportsCaptureAffiliationAndCurrentPublicIdentityChangesAfterDeparture()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        var resident = world.Services.GetRequiredService<LiveWorld>();
        var pilots = world.Services.GetRequiredService<IPlayerCorporations>();
        Assert.Equal(setup.Alliance, pilots.Read(setup.Member)!.AllianceId);
        long ship = world.Store.FindPilot(1000002)!.ShipId;
        using (var action = resident.Economy.Begin(TransactionParticipants.None))
        {
            var party = PilotInteractionHistoryStore.Player(action, action.Pilots.Find(Other)!, ship);
            Assert.Equal(setup.Alliance, party.AllianceId);
            PilotInteractionHistoryStore.Save(action, "alliance-event", new(0, Other, clock.Now, 30000142, party, party, [], []));
            action.Commit();
        }
        world.Services.GetRequiredService<IAllianceMembership>().Remove(Other, setup.Member);
        Assert.Null(pilots.Read(setup.Member)!.AllianceId);
        var report = Assert.Single(world.Services.GetRequiredService<IPilotInteractionHistory>().Combat(Other, 25, null));
        Assert.Equal(setup.Alliance, report.Victim.AllianceId);
        var native = Assert.IsType<BlueInstance>(new ShipCombatReportRow(report).ToBlue());
        Assert.Equal(setup.Alliance, Assert.IsType<Dictionary<string, object?>>(native.State)["victimAllianceID"]);
        var pilot = world.Store.FindPilot(1000002)!;
        Assert.Null(pilot.Identity.AllianceId);
        var card = Assert.IsType<BlueInstance>(new CharacterSelectionCard(pilot, 0, null, null, 0).ToBlue());
        Assert.Null(Assert.IsType<Dictionary<string, object?>>(card.State)["allianceID"]);
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        Assert.Equal(
            setup.Alliance,
            Assert.Single(reopened.GetRequiredService<IPilotInteractionHistory>().Combat(Other, 25, null)).Victim.AllianceId
        );
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public async Task PersonalAndCorporationContactsAcceptAllianceOwnersWithoutCharacterOnlyActions()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        var personal = world.Services.GetRequiredService<IPilotContacts>();
        var corporation = world.Services.GetRequiredService<ICorporationContacts>();
        personal.Add(Other, setup.Alliance, 5, false);
        corporation.SetStanding(Other, [setup.Alliance], -5);
        Assert.Throws<InvalidDataException>(() => personal.Edit(Other, setup.Alliance, 10, true));
        Assert.Throws<InvalidDataException>(() => personal.Edit(Other, setup.Alliance, 10, false, "Notify"));
        Assert.Throws<InvalidDataException>(() => personal.SetBlocked(Other, [setup.Alliance], true));
        Assert.Equal(5, Assert.Single(personal.Read(Other).Addresses).Standing);
        long label = personal.CreateLabel(Other, "Alliances", null);
        personal.SetLabels(Other, [setup.Alliance], label, true);
        Assert.Equal(label, Assert.Single(personal.Read(Other).Addresses).LabelMask);
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        Assert.Equal(5, Assert.Single(reopened.GetRequiredService<IPilotContacts>().Read(Other).Addresses).Standing);
        Assert.Equal(-5, Assert.Single(reopened.GetRequiredService<ICorporationContacts>().Read(Other).Contacts).Standing);
        reopened.GetRequiredService<IPilotContacts>().Delete(Other, [setup.Alliance]);
        Assert.Empty(reopened.GetRequiredService<IPilotContacts>().Read(Other).Addresses);
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public void CurrentAllianceIsPublishedInSelectionShowInfoGuestsAndSpaceContacts()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        using CorporationTestClient member = new(world, 1000002);
        var pilot = world.Store.FindPilot(1000002)!;
        var card = Assert.IsType<BlueInstance>(new CharacterSelectionCard(pilot, 0, null, null, 0).ToBlue());
        Assert.Equal(setup.Alliance, Assert.IsType<Dictionary<string, object?>>(card.State)["allianceID"]);
        var info = Assert.IsType<CharacterCorporationInfo>(
            Assert.IsType<PilotCallResult.Success>(member.CallService("corpRegistry", "GetInfoWindowDataForChar", [Other])).Reply
        );
        Assert.Equal(setup.Alliance, info.AllianceId);
        var corporation = new PlayerCorporationReply(world.Services.GetRequiredService<IPlayerCorporations>().Read(setup.Member)!);
        var row = Assert.IsType<BlueConstructor>(corporation.ToBlue());
        Assert.Equal(setup.Alliance, Assert.IsType<object?[]>(row.Arguments[1])[16]);
        StationGuest guest = new(Other, setup.Member, setup.Alliance);
        Assert.Equal(setup.Alliance, Assert.IsType<object?[]>(guest.ToBlue())[2]);
        ShipContact ship = new(PilotContact.From(pilot), new(0, 0, 0), null, 0, null);
        var slim = Assert.IsType<BlueInstance>(new ShipSlimItem(ship).ToBlue());
        Assert.Equal(setup.Alliance, Assert.IsType<Dictionary<string, object?>>(slim.State)["allianceID"]);
        world.Services.GetRequiredService<IAllianceMembership>().Remove(Other, setup.Member);
        // Public reads must not wait for the existing selected session to refresh.
        var after = Assert.IsType<CharacterCorporationInfo>(
            Assert.IsType<PilotCallResult.Success>(member.CallService("corpRegistry", "GetInfoWindowDataForChar", [Other])).Reply
        );
        Assert.Null(after.AllianceId);
    }

    [Fact]
    public void RecruitmentRowsAndStandaloneFilterFollowCommittedAllianceMembership()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        world
            .Services.GetRequiredService<ICorporationWallet>()
            .Transfer(
                Other,
                new(false, CorporationWalletRules.MasterKey, setup.Member, CorporationWalletRules.MasterKey, 200_000_000, "Recruitment")
            );
        var recruitment = world.Services.GetRequiredService<ICorporationRecruitment>();
        recruitment.Create(Other, new("Recruiting", "Operations", 0, 0, 0, 0, 0, [Other]), 1);
        CorporationRecruitmentSearch query = new(0, 0, true, false, null, 1, 100, 1, 1, 0, 0);
        Assert.Empty(recruitment.Search(query));
        var ad = Assert.Single(recruitment.Search(query with { ExcludeAlliances = false })).Ad;
        Assert.Equal(setup.Alliance, ad.AllianceId);
        var row = Assert.IsType<BlueConstructor>(new CorporationRecruitmentRow(ad).ToBlue());
        Assert.Equal(setup.Alliance, Assert.IsType<object?[]>(row.Arguments[1])[2]);
        world.Services.GetRequiredService<IAllianceMembership>().Remove(Other, setup.Member);
        Assert.Null(Assert.Single(recruitment.Search(query)).Ad.AllianceId);
    }

    [Fact]
    public async Task Version177UpgradePreservesPersonalContactsAndRegistersExistingAllianceOwners()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        world.Services.GetRequiredService<IPilotContacts>().Add(Ceo, Other, 5, false);
        world.Services.Dispose();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw(
                """
                DROP TRIGGER DeletePilotContacts;
                DROP TRIGGER DeleteCorporationContacts;
                CREATE TABLE WorldContactLegacy(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    ContactId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Standing REAL NOT NULL, InWatchlist INTEGER NOT NULL, LabelMask INTEGER NOT NULL,
                    PRIMARY KEY(PilotId,ContactId));
                INSERT INTO WorldContactLegacy SELECT * FROM WorldContact;
                DROP TABLE WorldContact;
                ALTER TABLE WorldContactLegacy RENAME TO WorldContact;
                CREATE INDEX IX_WorldContact_ContactId ON WorldContact(ContactId);
                PRAGMA user_version=177;
                """
            );
            saved.Database.ExecuteSql($"DELETE FROM WorldInventoryOwner WHERE Id={setup.Alliance}");
        }
        WorldInitialization.Open(world.Path);
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var personal = reopened.GetRequiredService<IPilotContacts>();
        Assert.Equal(Other, Assert.Single(personal.Read(Ceo).Addresses).ContactId);
        personal.Add(Ceo, setup.Alliance, 10, false);
        reopened.GetRequiredService<IPilotSavedFittings>().Save(Ceo, Plan(), ownerId: setup.Alliance);
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Theory]
    [InlineData(AccessGroupRole.Member, AccessGroupRole.Excluded, AccessGroupRole.Member, AccessGroupRole.Member)]
    [InlineData(AccessGroupRole.None, AccessGroupRole.Excluded, AccessGroupRole.Member, AccessGroupRole.Excluded)]
    [InlineData(AccessGroupRole.None, AccessGroupRole.None, AccessGroupRole.Excluded, AccessGroupRole.Excluded)]
    [InlineData(AccessGroupRole.None, AccessGroupRole.None, AccessGroupRole.None, AccessGroupRole.Member)]
    public void AccessIdentityPrecedencePreservesSpecificExceptions(
        AccessGroupRole pilot,
        AccessGroupRole corporation,
        AccessGroupRole alliance,
        AccessGroupRole expected
    )
    {
        AccessGroupMember[] members = [new(Ceo, pilot), new(1, corporation), new(2, alliance), new(0, AccessGroupRole.Member)];
        Assert.Equal(expected, AccessGroupRules.Resolve(members.Where(m => m.Role != AccessGroupRole.None).ToArray(), Ceo, 1, 2));
    }

    [Fact]
    public void AllianceCreationSkipsExistingOwnerIdentifiers()
    {
        using TestWorld world = new(liveWorld: true);
        Corporation(world);
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new InventoryOwner { Id = AllianceRules.FirstAllianceId });
            action.Commit();
        }
        Assert.Equal(AllianceRules.FirstAllianceId + 1, world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id);
    }

    [Fact]
    public void AccessListsCanRemoveArchivedAllianceEntriesAfterDissolution()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Corporation(world);
        int alliance = world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id;
        var groups = world.Services.GetRequiredService<IPilotAccessGroups>();
        var group = groups.Create(Ceo, "Alliance access", "");
        groups.ChangeMembers(Ceo, group.Id, [new(alliance, AccessGroupRole.Member)]);
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        membership.Remove(Ceo, corporation);
        clock.AdvanceWallTime(TimeSpan.FromDays(2));
        membership.Advance();
        Assert.NotNull(world.Services.GetRequiredService<IAlliances>().Read(alliance)!.ClosedAt);
        groups.ChangeMembers(Ceo, group.Id, [new(alliance, AccessGroupRole.Member)], remove: true);
        Assert.DoesNotContain(groups.Members(Ceo, group.Id), member => member.OwnerId == alliance);
    }
}
