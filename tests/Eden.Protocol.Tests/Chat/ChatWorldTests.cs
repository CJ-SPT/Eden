using Eden.Gameplay.Chat;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Chat;
using Eden.Server.Launcher;

namespace Eden.Protocol.Tests.Chat;

public sealed class ChatWorldTests
{
    private static readonly ChatRoom Local = new(ChatRoomKind.Local, 30000142);
    private static readonly ChatPilot First = new(90000001, "First pilot", 1000044, 30000142, 0);
    private static readonly ChatPilot Second = new(90000002, "Second pilot", 1000044, 30000142, 0);

    [Fact]
    public void TwoPilotsSeeEachOtherAndReceiveOneOrderedEchoEach()
    {
        ChatWorld world = new();
        Guid firstSession = Guid.NewGuid(),
            secondSession = Guid.NewGuid(),
            first = Guid.NewGuid(),
            second = Guid.NewGuid();
        world.Update(firstSession, First);
        world.Update(secondSession, Second);
        world.Connect(firstSession, first);
        world.Connect(secondSession, second);
        Assert.Single(world.Join(first, Local, "first"));
        var joined = world.Join(second, Local, "second");
        Assert.Equal(
            new[] { First.Id, Second.Id },
            joined.Where(e => e.Recipient == second).Select(e => Assert.IsType<ChatEvent.Presence>(e.Event).Pilot.Id)
        );
        Assert.Single(joined, e => e.Recipient == first && e.Event is ChatEvent.Presence { Available: true });
        Assert.Single(world.Join(second, Local, "retry"));
        const string text = "こんにちは 🚀 <b>hello</b> & friends";
        var sent = world.Send(first, Local, text, "message-1", 0);
        Assert.Equal(new[] { first, second }, sent.Select(e => e.Recipient));
        Assert.All(sent, e => Assert.Equal(new ChatEvent.Message(Local, First, text, "message-1"), e.Event));
        Assert.Equal(2, world.Members(first, Local).Count);
        Assert.Equal(new[] { First.Id, Second.Id }, world.Members(first, Local).Select(p => p.Id));
    }

    [Fact]
    public void SystemChangesAndReconnectsRemoveOldMembershipWithoutGhostsOrCrossSystemMessages()
    {
        ChatWorld world = new();
        Guid a = Guid.NewGuid(),
            b = Guid.NewGuid(),
            first = Guid.NewGuid(),
            second = Guid.NewGuid();
        world.Update(a, First);
        world.Update(b, Second);
        world.Connect(a, first);
        world.Connect(b, second);
        world.Join(first, Local, "a");
        world.Join(second, Local, "b");
        var departure = world.Update(b, Second with { SystemId = 30000144 });
        Assert.Single(departure, e => e.Recipient == first && e.Event is ChatEvent.Presence { Available: false });
        Assert.Throws<InvalidDataException>(() => world.Send(second, Local, "forged old Local", "bad", 0));
        Assert.Throws<InvalidDataException>(() => world.Join(second, Local, "bad"));
        ChatRoom destination = new(ChatRoomKind.Local, 30000144);
        world.Join(second, destination, "new");
        Assert.Single(world.Send(first, Local, "old system", "old", 0));
        Guid replacement = Guid.NewGuid();
        var replaced = world.Connect(b, replacement);
        Assert.Single(replaced, e => e.Recipient == second && e.Event is ChatEvent.Closed);
        Assert.Throws<InvalidDataException>(() => world.Send(second, destination, "stale", "stale", 0));
        world.Join(replacement, destination, "reconnect");
        Assert.Single(world.Members(replacement, destination));
        world.Update(b, null);
        Assert.Null(world.ConnectedPilot(replacement));
        Assert.Single(world.Members(first, Local));
    }

    [Theory]
    [InlineData(ChatRoomKind.Wormhole)]
    [InlineData(ChatRoomKind.Triglavian)]
    public void DelayedLocalDoesNotDiscloseSilentPilots(ChatRoomKind kind)
    {
        ChatWorld world = new();
        Guid a = Guid.NewGuid(),
            b = Guid.NewGuid(),
            first = Guid.NewGuid(),
            second = Guid.NewGuid();
        world.Update(a, First with { LocalKind = kind });
        world.Update(b, Second with { LocalKind = kind });
        world.Connect(a, first);
        world.Connect(b, second);
        ChatRoom room = new(kind, First.SystemId);
        world.Join(first, room, "a");
        var joined = world.Join(second, room, "b");
        Assert.Single(joined);
        Assert.Equal(second, joined[0].Recipient);
        Assert.Single(world.Members(first, room));
        var spoken = world.Send(second, room, "revealed by speaking", "msg", 0);
        Assert.Equal(2, spoken.Count(e => e.Event is ChatEvent.Presence { Available: true }));
        Assert.Equal(2, spoken.Count(e => e.Event is ChatEvent.Message));
        Assert.Equal(2, world.Members(first, room).Count);
        Assert.Single(world.Members(second, room));
        Assert.Throws<InvalidDataException>(() => world.Join(first, Local, "wrong category"));
    }

    [Fact]
    public void PrivateAndCorporationAccessRejectForgedMembershipWithoutChangingRecipients()
    {
        ChatWorld world = new();
        Guid a = Guid.NewGuid(),
            b = Guid.NewGuid(),
            c = Guid.NewGuid();
        Guid first = Guid.NewGuid(),
            second = Guid.NewGuid(),
            outsider = Guid.NewGuid();
        world.Update(a, First);
        world.Update(b, Second);
        world.Update(c, Second with { Id = 90000003, CorporationId = 1000001 });
        world.Connect(a, first);
        world.Connect(b, second);
        world.Connect(c, outsider);
        var room = world.CreatePrivate(a, Second.Id);
        world.Join(first, room, "a");
        Assert.Throws<InvalidDataException>(() => world.Join(outsider, room, "forged"));
        Assert.Throws<InvalidDataException>(() => world.AllowPrivate(b, room, 90000003));
        Assert.Throws<InvalidDataException>(() => world.Join(outsider, new(ChatRoomKind.Corporation, First.CorporationId), "forged"));
        var invite = Assert.Single(world.Invite(first, room, Second.Id));
        Assert.Equal(second, invite.Recipient);
        world.Join(second, room, "b");
        Assert.Equal(2, world.Send(first, room, "private", "p", 0).Count);
        Assert.Throws<InvalidDataException>(() => world.Invite(outsider, room, Second.Id));
    }

    [Fact]
    public void InvalidMessagesAndRateLimitsDoNotBroadcastAndRecoverAtExactBoundary()
    {
        ChatWorld world = new();
        Guid session = Guid.NewGuid(),
            connection = Guid.NewGuid();
        world.Update(session, First);
        world.Connect(session, connection);
        world.Join(connection, Local, "join");
        foreach (string text in new[] { "", "   ", "bad\u0001", new string('x', ChatRules.MaximumMessageLength + 1) })
        {
            Assert.Throws<InvalidDataException>(() => world.Send(connection, Local, text, "bad", 0));
        }
        for (int index = 0; index < ChatRules.MessagesPerSecond; index++)
        {
            Assert.Single(world.Send(connection, Local, "valid", index.ToString(), 0));
        }
        Assert.Throws<InvalidDataException>(() => world.Send(connection, Local, "too early", "bad", TimeSpan.TicksPerSecond - 1));
        Assert.Single(world.Send(connection, Local, "boundary", "ok", TimeSpan.TicksPerSecond));
    }

    [Fact]
    public void TokensExpireAreSingleUseAndCannotAuthenticateAnotherPilotOrOutliveSelection()
    {
        TestClock clock = new();
        ChatRuntime runtime = new(new LauncherOptions(), clock);
        Guid session = Guid.NewGuid();
        runtime.Update(session, First);
        string token = runtime.IssueToken(session);
        Assert.Throws<InvalidDataException>(() => runtime.Authenticate(Second.Id.ToString(), token));
        var connected = runtime.Authenticate(First.Id.ToString(), token);
        Assert.Throws<InvalidDataException>(() => runtime.Authenticate(First.Id.ToString(), token));
        string expired = runtime.IssueToken(session);
        clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        Assert.Throws<InvalidDataException>(() => runtime.Authenticate(First.Id.ToString(), expired));
        string revoked = runtime.IssueToken(session);
        runtime.Update(session, null);
        Assert.Throws<InvalidDataException>(() => runtime.Authenticate(First.Id.ToString(), revoked));
        Assert.Contains(Drain(connected), e => e.Event is ChatEvent.Closed);
        Assert.Throws<InvalidDataException>(() => runtime.IssueToken(session));
    }

    [Fact]
    public async Task ConcurrentPilotsReceiveTheSameOrderedMessagesExactlyOnce()
    {
        ChatRuntime runtime = new(new LauncherOptions(), new TestClock());
        Guid a = Guid.NewGuid(),
            b = Guid.NewGuid();
        runtime.Update(a, First);
        runtime.Update(b, Second);
        var first = runtime.Authenticate(First.Id.ToString(), runtime.IssueToken(a));
        var second = runtime.Authenticate(Second.Id.ToString(), runtime.IssueToken(b));
        runtime.Join(first.Id, Local, "a");
        runtime.Join(second.Id, Local, "b");
        Drain(first);
        Drain(second);
        await Task.WhenAll(
            new[] { first, second }.Select(connection =>
                Task.Run(() =>
                {
                    for (int index = 0; index < 3; index++)
                    {
                        runtime.Send(connection.Id, Local, "concurrent message", connection.Pilot.Id + "-" + index);
                    }
                })
            )
        );
        var one = Drain(first).Select(delivery => Assert.IsType<ChatEvent.Message>(delivery.Event)).ToArray();
        var two = Drain(second).Select(delivery => Assert.IsType<ChatEvent.Message>(delivery.Event)).ToArray();
        Assert.Equal(6, one.Length);
        Assert.Equal(6, one.Select(message => message.Id).Distinct().Count());
        Assert.Equal(one.Select(message => message.Id), two.Select(message => message.Id));
        runtime.Update(a, null);
        runtime.Update(b, null);
    }

    [Fact]
    public void SelectingAnotherCharacterRevokesPreviouslyIssuedCredentials()
    {
        ChatRuntime runtime = new(new LauncherOptions(), new TestClock());
        Guid session = Guid.NewGuid();
        runtime.Update(session, First);
        string old = runtime.IssueToken(session);
        runtime.Update(session, Second);
        Assert.Throws<InvalidDataException>(() => runtime.Authenticate(Second.Id.ToString(), old));
        var current = runtime.Authenticate(Second.Id.ToString(), runtime.IssueToken(session));
        Assert.Equal(Second, current.Pilot);
        runtime.Update(session, null);
    }

    [Fact]
    public void PublicChannelCreationRejectsDuplicateNamesAndRestrictsCreationCount()
    {
        ChatWorld world = new();
        Guid session = Guid.NewGuid();
        world.Update(session, First);
        var room = world.CreatePublic(session, "Eden pilots");
        Assert.Equal("Eden pilots", Assert.Single(world.PublicRooms()).Name);
        Assert.Throws<InvalidDataException>(() => world.CreatePublic(session, "EDEN PILOTS"));
        Assert.Throws<InvalidDataException>(() => world.CreatePublic(session, "  "));
        for (int index = 1; index < ChatRules.MaximumCreatedRooms; index++)
        {
            world.CreatePublic(session, "Channel " + index);
        }
        Assert.Throws<InvalidDataException>(() => world.CreatePublic(session, "Too many"));
        Guid connection = Guid.NewGuid();
        world.Connect(session, connection);
        world.Join(connection, room, "join");
        Assert.Equal("Eden pilots", world.RoomInfo(connection, room).Name);
    }

    private static IReadOnlyList<ChatDelivery> Drain(Eden.Protocol.Chat.XmppChatConnection connection)
    {
        List<ChatDelivery> result = [];
        while (connection.Events.TryRead(out var item))
        {
            result.Add(item);
        }
        return result;
    }
}
