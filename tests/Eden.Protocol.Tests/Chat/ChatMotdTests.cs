using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Chat;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.Server.Chat;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Chat;

public sealed class ChatMotdTests
{
    [Fact]
    public async Task NativeCorporationMotdCallBroadcastsSubjectAndRejoinsReceiveSavedText()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        var runtime = world.Services.GetRequiredService<ChatRuntime>();
        var certificate = world.Services.GetRequiredService<ChatCertificate>();
        await using var server = world.Services.GetRequiredService<ChatServer>();
        await server.StartAsync(new(IPAddress.Loopback, 0));
        string channel = $"corp_{corporation}";
        string jid = channel + "@conference.eden.chat";
        const string text = "<b>Welcome \u661f</b> & pilots\nBring ships \ud83d\ude80";
        await using var first = await Connect(ceo);
        await using var second = await Connect(member);
        await Join(first, "first", "");
        await Join(second, "second", "");
        Assert.Same(PilotCallResult.None, ceo.CallService("XmppChatMgr", "SetMotd", [jid, text]));
        await Subject(first, text);
        await Subject(second, text);
        Assert.Equal(text, world.Services.GetRequiredService<ICorporationCommunications>().ReadMotd(corporation));
        await Join(second, "retry", text);
        await second.DisposeAsync();
        await using var reconnected = await Connect(member);
        await Join(reconnected, "reconnect", text);
        Assert.Same(PilotCallResult.None, ceo.CallService("XmppChatMgr", "SetMotd", [Encoding.UTF8.GetBytes(jid), Array.Empty<byte>()]));
        await Subject(first, "");
        await Subject(reconnected, "");
        Assert.Equal("", world.Services.GetRequiredService<ICorporationCommunications>().ReadMotd(corporation));

        Task<XmppChatTests.NativeClient> Connect(CorporationTestClient client)
        {
            var token = Assert.IsType<PilotCallResult.Success>(
                client.CallService("chatAuthenticationService", "GetAuthenticationToken", [])
            );
            return XmppChatTests.NativeClient.ConnectAsync(
                server.Endpoint!,
                certificate.Certificate,
                client.Context.ActiveFlight!.Pilot.PilotId,
                Eden.Protocol.Serialization.BlueMarshal.Text(token.Reply.ToBlue())
            );
        }

        async Task Join(XmppChatTests.NativeClient client, string id, string expected)
        {
            await client.JoinAsync(channel, id);
            await client.UntilAsync(e => (string?)e.Attribute("id") == id);
            await Subject(client, expected);
        }

        async Task Subject(XmppChatTests.NativeClient client, string expected)
        {
            var stanza = (await client.UntilAsync(e => e.Elements().Any(child => child.Name.LocalName == "subject")))[^1];
            Assert.Equal(jid, (string?)stanza.Attribute("from"));
            Assert.Equal("groupchat", (string?)stanza.Attribute("type"));
            Assert.Equal(expected, Assert.Single(stanza.Elements()).Value);
        }
    }

    [Theory]
    [InlineData(CorporationRole.ChatManager, true)]
    [InlineData(CorporationRole.Director, true)]
    [InlineData(CorporationRole.PersonnelManager, false)]
    [InlineData((CorporationRole)0, false)]
    public void MotdUsesCurrentCorporationAuthorityAndRejectsMalformedCallsWithoutChangingState(CorporationRole role, bool allowed)
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        var communications = world.Services.GetRequiredService<ICorporationCommunications>();
        string jid = $"corp_{corporation}@conference.eden.chat";
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(90000002, new((long)role))));
        var result = member.CallService("XmppChatMgr", "SetMotd", [jid, "Changed"]);
        Assert.Equal(allowed, result is PilotCallResult.Success);
        Assert.Equal(allowed ? "Changed" : "", communications.ReadMotd(corporation));
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [jid],
                [jid, 1L],
                [jid, "text", 1L],
                [$"corp_{corporation + 1}@conference.eden.chat", "Foreign"],
                [$"corp_{corporation}@conference.other", "Foreign"],
                ["local_30000142@conference.eden.chat", "Local"],
                ["alliance_99000001@conference.eden.chat", "Alliance"],
                [jid, new string('x', ChatRules.MaximumMotdLength + 1)],
                [jid, "bad\0text"],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(ceo.CallService("XmppChatMgr", "SetMotd", args));
        }
        Assert.IsType<PilotCallResult.Failure>(ceo.CallService("XmppChatMgr", "SetMotd", [jid, "No"], new() { ["ownerID"] = 1L }));
        Assert.Equal(allowed ? "Changed" : "", communications.ReadMotd(corporation));
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(90000002, new())));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("XmppChatMgr", "SetMotd", [jid, "Revoked"]));
        Assert.Equal(allowed ? "Changed" : "", communications.ReadMotd(corporation));
        ceo.Context.LogOff();
        Assert.Same(PilotCallResult.Unsupported, ceo.CallService("XmppChatMgr", "SetMotd", [jid, "Logged off"]));
    }

    [Fact]
    public void MotdPersistsWithOtherCommunicationsAcrossCheckpointsAndOldRecordsDefaultToEmpty()
    {
        var old = JsonSerializer.Deserialize<CorporationCommunications>(
            "{\"Bulletins\":[],\"WelcomeMail\":{\"Body\":\"Welcome\"},\"NextBulletinId\":1}"
        );
        Assert.Equal("", old!.Motd);
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var communications = world.Services.GetRequiredService<ICorporationCommunications>();
        long bulletin = communications.SaveBulletin(90000001, "Title", "Body", null, null);
        string text = new('\u661f', ChatRules.MaximumMotdLength);
        communications.SetMotd(90000001, corporation, text);
        communications.SetWelcomeMail(90000001, "Welcome");
        Assert.Equal(bulletin, Assert.Single(communications.Read(90000002).Bulletins).Id);
        world.Services.Dispose();
        using (var reopened = ServerServices.Build(new(world.Path), liveWorld: true))
        {
            var saved = reopened.GetRequiredService<ICorporationCommunications>();
            Assert.Equal(text, saved.ReadMotd(corporation));
            Assert.Equal("Welcome", saved.Read(90000002).WelcomeMail.Body);
            Assert.Equal(bulletin, Assert.Single(saved.Read(90000002).Bulletins).Id);
            saved.SetMotd(90000001, corporation, "");
        }
        using var cleared = ServerServices.Build(new(world.Path), liveWorld: true);
        Assert.Equal("", cleared.GetRequiredService<ICorporationCommunications>().ReadMotd(corporation));
    }

    [Fact]
    public async Task ConcurrentEditsAndJoinsDeliverSubjectsInCommittedOrder()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var runtime = world.Services.GetRequiredService<ChatRuntime>();
        Guid actor = Guid.NewGuid(),
            member = Guid.NewGuid();
        runtime.Update(actor, new(90000001, "CEO", corporation, 30000142, 0));
        runtime.Update(member, new(90000002, "Member", corporation, 30000142, 0));
        var first = runtime.Authenticate("90000001", runtime.IssueToken(actor));
        var second = runtime.Authenticate("90000002", runtime.IssueToken(member));
        ChatRoom room = new(ChatRoomKind.Corporation, corporation);
        runtime.Join(first.Id, room, "first");
        runtime.Join(second.Id, room, "second");
        Drain(first);
        Drain(second);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() => runtime.SetMotd(actor, room, $"Edit {index}"))));
        string[] one = Drain(first),
            two = Drain(second);
        Assert.Equal(8, one.Length);
        Assert.Equal(8, one.Distinct().Count());
        Assert.Equal(one, two);
        Assert.Equal(one[^1], world.Services.GetRequiredService<ICorporationCommunications>().ReadMotd(corporation));
        await Task.WhenAll(Task.Run(() => runtime.SetMotd(actor, room, "Final")), Task.Run(() => runtime.Join(second.Id, room, "retry")));
        Assert.Equal("Final", Drain(first)[^1]);
        Assert.Equal("Final", Drain(second)[^1]);

        static string[] Drain(XmppChatConnection connection)
        {
            List<string> subjects = [];
            while (connection.Events.TryRead(out var delivery))
            {
                if (delivery.Event is ChatEvent.Subject subject)
                {
                    subjects.Add(subject.Text);
                }
            }
            return subjects.ToArray();
        }
    }

    [Fact]
    public void ResidentMotdRemainsAvailableAfterFailedCheckpointAndRetry()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var communications = world.Services.GetRequiredService<ICorporationCommunications>();
        List<string> commands = [];
        using (SessionPerformance.Begin("resident MOTD", _ => { }, force: true, command: commands.Add))
        {
            communications.SetMotd(90000001, corporation, "Checkpoint text \u661f");
            Assert.Equal("Checkpoint text \u661f", communications.ReadMotd(corporation));
        }
        Assert.Empty(commands);
        var live = world.Services.GetRequiredService<LiveWorld>();
        using (var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            using var command = snapshot.Database.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER RejectMotd BEFORE INSERT ON WorldCorporation
                BEGIN SELECT RAISE(ABORT,'test MOTD checkpoint'); END
                """;
            command.ExecuteNonQuery();
            Assert.Throws<SqliteException>(() => snapshot.Project());
        }
        Assert.Equal("Checkpoint text \u661f", communications.ReadMotd(corporation));
        using var retry = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        retry.Project();
        using WorldDbContext saved = new(retry.Database);
        Assert.Equal("Checkpoint text \u661f", saved.Set<SavedCorporation>().Find(corporation)!.Snapshot(0).Details.Communications!.Motd);
    }

    [Fact]
    public void NpcCorporationMotdCannotBeEdited()
    {
        using TestWorld world = new(liveWorld: true);
        using CorporationTestClient pilot = new(world, 1000001);
        int corporation = pilot.Context.ActiveFlight!.Pilot.CorpId;
        Assert.IsType<PilotCallResult.Failure>(
            pilot.CallService("XmppChatMgr", "SetMotd", [$"corp_{corporation}@conference.eden.chat", "No"])
        );
    }

    [Fact]
    public void SubjectBroadcastIsLimitedToCurrentMembersOfItsRoom()
    {
        ChatWorld world = new();
        Guid ownerSession = Guid.NewGuid(),
            peerSession = Guid.NewGuid();
        Guid owner = Guid.NewGuid(),
            peer = Guid.NewGuid();
        ChatRoom room = new(ChatRoomKind.Corporation, 98000000);
        world.Update(ownerSession, new(90000001, "Owner", (int)room.Id, 30000142, 0));
        world.Update(peerSession, new(90000002, "Peer", (int)room.Id, 30000142, 0));
        world.Connect(ownerSession, owner);
        world.Connect(peerSession, peer);
        world.Join(owner, room, "owner");
        Assert.Equal(owner, Assert.Single(world.Subject(room, "First")).Recipient);
        world.Join(peer, room, "peer");
        Assert.Equal(2, world.Subject(room, "Both").Count);
        world.Update(peerSession, new(90000002, "Peer", 98000001, 30000142, 0));
        Assert.Equal(owner, Assert.Single(world.Subject(room, "Private")).Recipient);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<b>\u661f</b> & pilots\n\tReady\r\n")]
    public void PureMotdRulesAllowClearingAndRichUnicodeText(string text) => ChatRules.CheckMotd(text);
}
