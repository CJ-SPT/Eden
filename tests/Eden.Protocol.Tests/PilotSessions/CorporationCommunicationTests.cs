using Eden.Gameplay.Corporations;
using Eden.Gameplay.Mail;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationCommunicationTests
{
    [Theory]
    [InlineData(CorporationRole.ChatManager, true)]
    [InlineData(CorporationRole.PersonnelManager, false)]
    public void DelegatedCommunicationRolesHaveSeparateAuthority(CorporationRole role, bool bulletinAllowed)
    {
        using TestWorld db = new(liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(db);
        using CorporationTestClient ceo = new(db, 1000001);
        using CorporationTestClient member = new(db, 1000002);
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(90000002, new((long)role))));
        var bulletin = member.Call("AddBulletin", ["Delegated", "Body"]);
        var welcome = member.Call("SetCorpWelcomeMail", ["Delegated welcome"]);
        Assert.Equal(bulletinAllowed, bulletin is PilotCallResult.Success);
        Assert.Equal(!bulletinAllowed, welcome is PilotCallResult.Success);
        Assert.Equal(bulletinAllowed ? 1 : 0, db.Services.GetRequiredService<ICorporationCommunications>().Read(90000001).Bulletins.Count);
    }

    [Fact]
    public void NpcCorporationsHaveEmptyReadOnlyCommunicationPanels()
    {
        using TestWorld db = new(liveWorld: true);
        using CorporationTestClient member = new(db, 1000001);
        Assert.Empty(Assert.IsType<List<object?>>(member.Reply("GetBulletins", [])));
        var mail = BlueRead.Fields(Assert.IsType<BlueInstance>(member.Reply("GetCorpWelcomeMail", [])).State);
        Assert.Equal("", BlueMarshal.Text(mail["welcomeMail"]));
        Assert.IsType<PilotCallResult.Failure>(member.Call("SetCorpWelcomeMail", ["No"]));
        Assert.IsType<PilotCallResult.Failure>(member.Call("AddBulletin", ["No", "No"]));
    }

    [Fact]
    public void BulletinEditingReorderingAndDeletionRespectRevisionsAndMembership()
    {
        using TestWorld db = new(liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(db);
        using CorporationTestClient ceo = new(db, 1000001);
        using CorporationTestClient member = new(db, 1000002);
        var store = db.Services.GetRequiredService<ICorporationCommunications>();
        long first = Assert.IsType<long>(ceo.Reply("AddBulletin", ["First 星", "<b>Body</b>"]));
        long second = Assert.IsType<long>(ceo.Reply("AddBulletin", ["Second", "Body 2"]));
        var initial = store.Read(90000001).Bulletins.First();
        var reply = Assert.IsType<CorporationBulletinsReply>(Assert.IsType<PilotCallResult.Success>(member.Call("GetBulletins", [])).Reply);
        Assert.Equal(2, reply.Bulletins.Count);
        Assert.NotEmpty(BlueMarshal.Encode(reply));
        var row = Assert.IsType<BlueConstructor>(Assert.IsType<List<object?>>(reply.ToBlue())[0]);
        Assert.Equal("blue.DBRow", row.Name);
        Assert.Equal("First 星", Assert.IsType<object?[]>(row.Arguments[1])[2]);
        Assert.IsType<PilotCallResult.Failure>(member.Call("AddBulletin", ["Denied", "Body"]));
        Assert.IsType<PilotCallResult.Failure>(member.Call("DeleteBulletin", [first]));
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("AddBulletin", ["Wrong", "Body"], new() { ["bulletinID"] = first }));
        Assert.IsType<PilotCallResult.Success>(
            ceo.Call("AddBulletin", ["Edited", "New body"], new() { ["bulletinID"] = first, ["editDateTime"] = initial.EditedAt })
        );
        Assert.IsType<PilotCallResult.Failure>(
            ceo.Call("AddBulletin", ["Stale", "Lost edit"], new() { ["bulletinID"] = first, ["editDateTime"] = initial.EditedAt })
        );
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("UpdateBulletinOrder", [new List<object?> { first, first }]));
        Assert.Null(ceo.Reply("UpdateBulletinOrder", [new List<object?> { second, first }]));
        Assert.Equal(new[] { second, first }, store.Read(90000002).Bulletins.OrderBy(b => b.Order).Select(b => b.Id));
        Assert.Equal("Edited", store.Read(90000001).Bulletins.Single(b => b.Id == first).Title);
        Assert.Null(ceo.Reply("DeleteBulletin", [first]));
        Assert.Equal(second, Assert.Single(store.Read(90000002).Bulletins).Id);
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("DeleteBulletin", [first]));
    }

    [Fact]
    public void LimitsAndUnknownKeywordsRejectWithoutChangingBulletins()
    {
        using TestWorld db = new(liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(db);
        using CorporationTestClient ceo = new(db, 1000001);
        var store = db.Services.GetRequiredService<ICorporationCommunications>();
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("AddBulletin", ["Valid", "Body"], new() { ["ownerID"] = 1L }));
        Assert.Throws<InvalidDataException>(() => store.SaveBulletin(90000001, "", "Body", null, null));
        Assert.Throws<InvalidDataException>(() => store.SaveBulletin(90000001, new string('x', 101), "Body", null, null));
        Assert.Throws<InvalidDataException>(() => store.SaveBulletin(90000001, "Title", new string('x', 4001), null, null));
        for (int i = 0; i < CorporationCommunicationRules.MaximumBulletins; i++)
        {
            store.SaveBulletin(90000001, $"Bulletin {i}", "Body", null, null);
        }
        Assert.Throws<InvalidDataException>(() => store.SaveBulletin(90000001, "Overflow", "Body", null, null));
        Assert.Equal(CorporationCommunicationRules.MaximumBulletins, store.Read(90000001).Bulletins.Count);
    }

    [Fact]
    public void WelcomeMailAndBulletinsSurviveRestartAndJoiningDeliversMailOnce()
    {
        using TestWorld db = new(liveWorld: true);
        PlayerCorporationTests.Prepare(db);
        int id = db.Services.GetRequiredService<IPlayerCorporations>().Create(90000001, PlayerCorporationTests.Details()).Corporation.Id;
        var communications = db.Services.GetRequiredService<ICorporationCommunications>();
        long bulletin = communications.SaveBulletin(90000001, "Saved", "Persistent", null, null);
        communications.SetWelcomeMail(90000001, "Welcome 星 to our corporation!");
        var invitation = db.Services.GetRequiredService<ICorporationMembership>().Invite(90000001, 90000002);
        db.Services.Dispose();
        using (var reopened = ServerServices.Build(new(db.Path), liveWorld: true))
        {
            Assert.Equal(bulletin, Assert.Single(reopened.GetRequiredService<ICorporationCommunications>().Read(90000001).Bulletins).Id);
            var membership = reopened.GetRequiredService<ICorporationMembership>();
            var join = new CorporationApplicationUpdate(
                invitation.Id,
                invitation.PilotId,
                id,
                invitation.Text,
                CorporationApplicationStatus.Joined,
                "",
                invitation.AppliedAt
            );
            membership.Update(90000002, join);
            Assert.Throws<InvalidDataException>(() => membership.Update(90000002, join));
            var mail = reopened.GetRequiredService<IPilotMail>();
            var header = Assert.Single(mail.Sync(90000002, null, 0).New);
            Assert.Equal(90000001, header.SenderId);
            Assert.Equal("Welcome 星 to our corporation!", mail.Body(90000002, header.Id, false, Guid.Empty));
            var sent = mail.Sync(90000001, null, 0);
            Assert.Equal(header.Id, Assert.Single(sent.New).Id);
            Assert.Equal((long)SystemMailLabel.Sent, Assert.Single(sent.Status).Labels);
            Assert.True(Assert.Single(sent.Status).Status.HasFlag(MailStatus.Automated));
        }
        using var persisted = ServerServices.Build(new(db.Path), liveWorld: true);
        Assert.Single(persisted.GetRequiredService<IPilotMail>().Sync(90000002, null, 0).New);
        Assert.Equal(
            "Welcome 星 to our corporation!",
            persisted.GetRequiredService<ICorporationCommunications>().Read(90000002).WelcomeMail.Body
        );
    }

    [Fact]
    public void WelcomeMailRequiresPersonnelAuthorityAndNotifiesConnectedMembers()
    {
        using TestWorld db = new(liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(db);
        using CorporationTestClient ceo = new(db, 1000001);
        using CorporationTestClient member = new(db, 1000002);
        Assert.IsType<PilotCallResult.Failure>(member.Call("SetCorpWelcomeMail", ["Denied"]));
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("SetCorpWelcomeMail", [new string('x', MailRules.MaximumTaggedBody + 1)]));
        Assert.Null(ceo.Reply("SetCorpWelcomeMail", ["Welcome aboard"]));
        member.Advance();
        var changed = Assert.Single(member.Context.Notifications.OfType<CorporationWelcomeMailChanged>());
        Assert.Equal(90000001, changed.Mail.EditorId);
        var data = BlueRead.Fields(Assert.IsType<BlueInstance>(member.Reply("GetCorpWelcomeMail", [])).State);
        Assert.Equal("Welcome aboard", BlueMarshal.Text(data["welcomeMail"]));
        Assert.Equal(changed.Mail.EditedAt, data["changeDate"]);
    }
}
