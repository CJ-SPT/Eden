using Eden.Data.World.Corporations;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CorporationMembershipTests
{
    [Fact]
    public void InvitationSurvivesCheckpointAndAcceptanceSurvivesRestart()
    {
        using TestWorld db = new(liveWorld: true);
        PlayerCorporationTests.Prepare(db);
        int id = db
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(Ceo, PlayerCorporationTests.Details() with { ApplicationsEnabled = false })
            .Corporation.Id;
        var before = db.Store.FindPilot(1000002)!;
        var invitation = db.Services.GetRequiredService<ICorporationMembership>().Invite(Ceo, Applicant);
        db.Services.Dispose();
        using (var reopened = ServerServices.Build(new(db.Path), liveWorld: true))
        {
            var membership = reopened.GetRequiredService<ICorporationMembership>();
            Assert.Equal(invitation, Assert.Single(membership.Applications(Applicant, false)));
            Assert.Equal(NpcCorporation, reopened.GetRequiredService<IPlayerCorporations>().Identity(Applicant).Id);
            membership.Update(Applicant, Change(invitation, CorporationApplicationStatus.Joined));
        }
        using var accepted = ServerServices.Build(new(db.Path), liveWorld: true);
        Assert.Equal(id, accepted.GetRequiredService<IPlayerCorporations>().Identity(Applicant).Id);
        Assert.Equal(
            CorporationApplicationStatus.Joined,
            Assert.Single(accepted.GetRequiredService<ICorporationMembership>().Applications(Applicant, false)).Status
        );
        Assert.Equal(0, accepted.GetRequiredService<IPilotWalletReader>().Balance(Applicant));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(Applicant, saved.Ships.Find(before.ShipId)!.OwnerId);
        Assert.Equal(before.LocationId, accepted.GetRequiredService<SqliteWorld>().FindPilot(1000002)!.LocationId);
    }

    [Theory]
    [InlineData(CorporationApplicationStatus.Withdrawn)]
    [InlineData(CorporationApplicationStatus.RejectedByApplicant)]
    [InlineData(CorporationApplicationStatus.RejectedByCorporation)]
    public void DuplicateInvitationsDoNotRepeatEventsAndClosedInvitationsCanBeReissued(CorporationApplicationStatus status)
    {
        using TestWorld db = new(liveWorld: true);
        PlayerCorporationTests.Prepare(db);
        db.Services.GetRequiredService<IPlayerCorporations>().Create(Ceo, PlayerCorporationTests.Details());
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var invitation = membership.Invite(Ceo, Applicant);
        long head = membership.Head(Applicant);
        Assert.Equal(invitation, membership.Invite(Ceo, Applicant));
        Assert.Equal(head, membership.Head(Applicant));
        Assert.Single(membership.Applications(Applicant, false));
        membership.Update(status == CorporationApplicationStatus.RejectedByCorporation ? Ceo : Applicant, Change(invitation, status));
        Assert.Equal(NpcCorporation, db.Store.FindPilot(1000002)!.CorpId);
        var replacement = membership.Invite(Ceo, Applicant);
        Assert.NotEqual(invitation.Id, replacement.Id);
        Assert.Equal(CorporationApplicationStatus.Invited, replacement.Status);
    }

    [Fact]
    public void InvitingAnApplicantPreservesTheirStatementAndOffersTheExistingApplication()
    {
        using TestWorld db = new(liveWorld: true);
        PlayerCorporationTests.Prepare(db);
        int id = db.Services.GetRequiredService<IPlayerCorporations>().Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var application = membership.Apply(Applicant, id, "Explorer — 探索");
        var invitation = membership.Invite(Ceo, Applicant);
        Assert.Equal(application.Id, invitation.Id);
        Assert.Equal(application.Text, invitation.Text);
        Assert.Equal(application.AppliedAt, invitation.AppliedAt);
        Assert.Equal(CorporationApplicationStatus.Offered, invitation.Status);
        Assert.Single(membership.Applications(Applicant, false));
    }

    [Fact]
    public void InvitationRejectsUnauthorizedMalformedAndFullCorporationRequestsWithoutChanges()
    {
        using TestWorld db = new(liveWorld: true);
        PlayerCorporationTests.Prepare(db);
        int id = db.Services.GetRequiredService<IPlayerCorporations>().Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        long head = membership.Head(Applicant);
        using Client ceo = new(db, 1000001);
        using Client applicant = new(db, 1000002);
        foreach (object?[] args in new object?[][] { [], [null], ["90000002"], [0L], [-1L], [long.MaxValue], [Applicant, Ceo], [Ceo] })
        {
            Assert.IsType<PilotCallResult.Failure>(ceo.Call("InsertInvitation", args));
        }
        Assert.IsType<PilotCallResult.Failure>(applicant.Call("InsertInvitation", [Applicant]));
        using (var world = LiveWorld.Find(db.Path)!.Economy.Begin(TransactionParticipants.None))
        {
            world.Set<SavedCorporation>().Find(id)!.MemberLimit = 1;
            world.Commit();
        }
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("InsertInvitation", [Applicant]));
        Assert.Empty(membership.Applications(Applicant, false));
        Assert.Equal(head, membership.Head(Applicant));
        Assert.Equal(NpcCorporation, db.Store.FindPilot(1000002)!.CorpId);
    }

    [Fact]
    public void PersonnelManagerCanInviteButAnOrdinaryMemberCannot()
    {
        using TestWorld db = new(liveWorld: true);
        PlayerCorporationTests.Prepare(db);
        int id = db.Services.GetRequiredService<IPlayerCorporations>().Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var invitation = membership.Invite(Ceo, Applicant);
        membership.Update(Applicant, Change(invitation, CorporationApplicationStatus.Joined));
        TestCatalog.AddSchool(new WorldDatabase(db.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        long target = db.Services.GetRequiredService<ICharacterCreation>().Create(1000001, new("New Recruit", 1, 1, 1, 64, [1], [2], 18));
        Assert.Throws<InvalidDataException>(() => membership.Invite(Applicant, target));
        using Client ceo = new(db, 1000001);
        ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Applicant, new((long)CorporationRole.PersonnelManager)));
        var recruited = membership.Invite(Applicant, target);
        Assert.Equal(id, recruited.CorporationId);
        Assert.Equal(target, recruited.PilotId);
        Assert.Equal(CorporationApplicationStatus.Invited, recruited.Status);
    }

    [Fact]
    public void DirectInvitationBindsAndPublishesAnOfferWithoutJoiningTheTarget()
    {
        using TestWorld db = new(liveWorld: true);
        PlayerCorporationTests.Prepare(db);
        int id = db.Services.GetRequiredService<IPlayerCorporations>().Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        using Client ceo = new(db, 1000001);
        using Client applicant = new(db, 1000002);
        var bound = Assert.IsType<BoundPilotReply>(
            Assert
                .IsType<PilotCallResult.Success>(
                    ceo.Call("MachoBindObject", [(long)id, new object?[] { "InsertInvitation", new object?[] { Applicant }, null }])
                )
                .Reply
        );
        long invitation = Assert.IsType<long>(BlueMarshal.Decode(BlueMarshal.Encode(bound.Reply)));
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var application = Assert.Single(membership.Applications(Applicant, false));
        Assert.Equal(invitation, application.Id);
        Assert.Equal(CorporationApplicationStatus.Invited, application.Status);
        var applications = Assert.IsType<Dictionary<long, object?>>(applicant.Reply("GetMyApplications", []));
        var row = Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(applications[id])));
        Assert.Equal((long)CorporationApplicationStatus.Invited, BlueRead.Fields(row.State)["status"]);
        Assert.Equal(invitation, BlueRead.Fields(row.State)["applicationID"]);
        Assert.Equal(NpcCorporation, db.Store.FindPilot(1000002)!.CorpId);
        applicant.Advance();
        Assert.Contains(applicant.Context.Notifications, n => n.Name == "OnCorporationApplicationChanged");
        Assert.Contains(ceo.Context.Notifications, n => n.Name == "OnCorporationApplicationChanged");
        Assert.IsType<PilotCallResult.Failure>(
            ceo.Call("UpdateApplicationOffer", UpdateArgs(application, CorporationApplicationStatus.Joined))
        );
        Assert.Null(applicant.Reply("UpdateApplicationOffer", UpdateArgs(application, CorporationApplicationStatus.Joined)));
        Assert.Equal(id, applicant.Context.Pilot.CorpId);
        Assert.Equal(2, db.Services.GetRequiredService<IPlayerCorporations>().Read(id)!.MemberCount);
    }

    [Fact]
    public void CorporationWindowRegistryReadsIncludeTheBulletinPanel()
    {
        using TestWorld db = new(liveWorld: true);
        PlayerCorporationTests.Prepare(db);
        int id = db.Services.GetRequiredService<IPlayerCorporations>().Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        using Client ceo = new(db, 1000001);
        foreach (
            var method in new[] { "GetCorporation", "GetTitles", "GetEveOwners", "GetApplications", "GetMyApplications", "GetBulletins" }
        )
        {
            var reply = Assert.IsType<BoundPilotReply>(
                Assert
                    .IsType<PilotCallResult.Success>(
                        ceo.Call("MachoBindObject", [(long)id, new object?[] { method, Array.Empty<object?>(), null }])
                    )
                    .Reply
            );
            Assert.Equal("corpRegistry", ceo.Context.ResolveReference(reply.Reference.Id));
            Assert.NotEmpty(BlueMarshal.Encode(reply));
            if (method == "GetBulletins")
            {
                Assert.Empty(Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply))));
            }
        }
    }
}
