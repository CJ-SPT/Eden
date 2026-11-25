using Eden.Gameplay.Corporations;
using Eden.Gameplay.Mail;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class CorporationMailRestrictionTests
{
    [Fact]
    public async Task NativeCorporationMailRestrictionChecksCurrentRoleAndSurvivesCheckpoint()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Corporation(world);
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        var invite = membership.Invite(Ceo, Other);
        membership.Update(Other, new(invite.Id, Other, corporation, "", CorporationApplicationStatus.Joined, "", invite.AppliedAt));
        var mail = world.Services.GetRequiredService<IPilotMail>();
        using (CorporationTestClient director = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            Assert.Equal(false, member.Reply("DoesCorpRestrictCorpMails", []));
            long before = Delivered(Send(member, corporation, "Open to members"));
            Assert.Equal((long)corporation, Assert.Single(mail.Headers(Ceo, [before])).OrganizationId);
            Assert.IsType<PilotCallResult.Failure>(member.Call("RegisterNewCorpMailRestrictionSettings", [true]));
            Assert.IsType<PilotCallResult.Failure>(director.Call("RegisterNewCorpMailRestrictionSettings", [2L]));
            Assert.Null(director.Reply("RegisterNewCorpMailRestrictionSettings", [true]));
            Assert.Equal(true, member.Reply("DoesCorpRestrictCorpMails", []));

            long sentBeforeDenial = mail.Observe(Other);
            long receivedBeforeDenial = mail.Observe(Ceo);
            Assert.IsType<PilotCallResult.Failure>(Send(member, corporation, "Mixed recipients", Ceo));
            Assert.Equal(sentBeforeDenial, mail.Observe(Other));
            Assert.Equal(receivedBeforeDenial, mail.Observe(Ceo));
            Assert.Null(Assert.Single(mail.Headers(Ceo, [Delivered(Send(member, null, "Personal mail", Ceo))])).OrganizationId);
            Assert.Equal(
                (long)corporation,
                Assert.Single(mail.Headers(Other, [Delivered(Send(director, corporation, "Director"))])).OrganizationId
            );

            Assert.Null(
                director.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Other, new((long)CorporationRole.ChatManager)))
            );
            long officerMail = Delivered(Send(member, corporation, "Communications Officer"));
            Assert.Equal((long)corporation, Assert.Single(mail.Headers(Ceo, [officerMail])).OrganizationId);
            Assert.IsType<PilotCallResult.Failure>(member.Call("RegisterNewCorpMailRestrictionSettings", [false]));
            Assert.Null(director.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Other, new())));
            Assert.IsType<PilotCallResult.Failure>(Send(member, corporation, "Role revoked"));
        }

        await world.Services.GetRequiredService<LiveWorld>().FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        var administration = reopened.GetRequiredService<ICorporationAdministration>();
        var restored = reopened.GetRequiredService<IPilotMail>();
        MailDraft draft = new([], "After reload", "Corporation mail body.", null, null) { OrganizationId = corporation };
        Assert.True(administration.RestrictsCorpMails(Other));
        Assert.Throws<InvalidDataException>(() => restored.Send(Other, draft, Guid.NewGuid()));
        administration.SetCorpMailRestriction(Ceo, false);
        Assert.False(administration.RestrictsCorpMails(Other));
        long reopenedMail = restored.Send(Other, draft, Guid.NewGuid());
        Assert.Equal((long)corporation, Assert.Single(restored.Headers(Ceo, [reopenedMail])).OrganizationId);
    }

    private static PilotCallResult Send(CorporationTestClient sender, long? corporation, string subject, params long[] recipients) =>
        sender.CallService("mailMgr", "SendMail", [recipients.Cast<object?>().ToList(), null, corporation, subject, "Body", null, null]);

    private static long Delivered(PilotCallResult result) =>
        Assert.IsType<long>(BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply)));
}
