using Eden.Gameplay.Chat;
using Eden.Server.Chat;
using Eden.Server.Launcher;
using Eden.World.Fleets;

namespace Eden.Protocol.Tests.Chat;

public sealed class ChatWorldTests
{
    private static readonly ChatRoom Local = new(ChatRoomKind.Local, 30000142);
    private static readonly ChatPilot First = new(90000001, "First pilot", 1000044, 30000142, 0);
    private static readonly ChatPilot Second = new(90000002, "Second pilot", 1000044, 30000142, 0);

    [Fact]
    public void SimulatedLocalArrivalIncludesAnAuthoritativeRosterForNativeParticipantState()
    {
        ChatWorld world = new();
        Guid session = Guid.NewGuid(),
            connection = Guid.NewGuid();
        world.Update(session, First);
        world.Connect(session, connection);
        world.Join(connection, Local, "initial");
        var arrived = world.UpdateCapsuleer(Second);
        var roster = Assert.Single(arrived.Select(delivery => delivery.Event).OfType<ChatEvent.Roster>());
        Assert.Equal(new[] { First.Id, Second.Id }, roster.Pilots.Select(pilot => pilot.Id));
    }

    [Fact]
    public void PopulationBatchesPublishOneCompleteRosterPerObserverAndKeepDelayedLocalPrivate()
    {
        ChatWorld world = new();
        Guid session = Guid.NewGuid(),
            connection = Guid.NewGuid(),
            second = Guid.NewGuid(),
            observer = Guid.NewGuid();
        world.Update(session, First);
        world.Update(second, Second);
        world.Connect(session, connection);
        world.Connect(second, observer);
        world.Join(connection, Local, "first");
        world.Join(observer, Local, "second");
        ChatPilot[] pilots = Enumerable
            .Range(0, 1000)
            .Select(i => new ChatPilot(91000000 + i, $"Visitor {i}", 1000044, First.SystemId, 0))
            .ToArray();
        var burst = world.UpdateCapsuleers(pilots);
        Assert.Equal(2, burst.Count);
        Assert.Same(burst[0].Event, burst[1].Event);
        Assert.All(burst, delivery => Assert.Equal(1002, Assert.IsType<ChatEvent.Roster>(delivery.Event).Pilots.Count));
        Assert.Equal(new[] { connection, observer }.Order(), burst.Select(e => e.Recipient).Order());
        Assert.Empty(world.UpdateCapsuleers(pilots));
        var departed = world.UpdateCapsuleers([pilots[0] with { SystemId = 30000143 }]);
        Assert.Equal(2, departed.Count(e => e.Event is ChatEvent.Presence { Available: false }));
        Assert.All(
            departed.Select(e => e.Event).OfType<ChatEvent.Roster>(),
            roster =>
            {
                Assert.Equal(1001, roster.Pilots.Count);
                Assert.DoesNotContain(roster.Pilots, p => p.Id == pilots[0].Id);
            }
        );
        var delayed = world.UpdateCapsuleers([pilots[1] with { SystemId = 31000001, LocalKind = ChatRoomKind.Wormhole }]);
        Assert.DoesNotContain(delayed, e => e.Event is ChatEvent.Roster { Room.Kind: ChatRoomKind.Wormhole });
    }

    [Theory]
    [InlineData(ChatRoomKind.Wormhole)]
    [InlineData(ChatRoomKind.Triglavian)]
    public void SimulatedPopulationBatchesDoNotRevealSilentPilotsInDelayedLocal(ChatRoomKind kind)
    {
        ChatWorld world = new();
        Guid session = Guid.NewGuid(),
            connection = Guid.NewGuid();
        var pilot = First with { LocalKind = kind };
        ChatRoom room = new(kind, pilot.SystemId);
        world.Update(session, pilot);
        world.Connect(session, connection);
        world.Join(connection, room, "delayed");
        Assert.Empty(world.UpdateCapsuleers([Second with { LocalKind = kind }]));
        Assert.DoesNotContain(world.Members(connection, room), member => member.Id == Second.Id);
    }

    [Fact]
    public void LocalTransferReusesItsRoomSlotAndPreservesOtherMemberships()
    {
        ChatWorld world = new();
        Guid session = Guid.NewGuid(),
            connection = Guid.NewGuid();
        world.Update(session, First);
        world.Connect(session, connection);
        world.Join(connection, Local, "local");
        List<ChatRoom> retained = [];
        for (int index = 1; index < ChatRules.MaximumJoinedRooms; index++)
        {
            var room = world.CreatePublic(session, "Retained " + index);
            retained.Add(room);
            world.Join(connection, room, "public");
        }
        ChatPilot moved = First with { SystemId = 30000144 };
        world.Update(session, moved);
        Assert.Equal(moved, Assert.Single(world.Members(connection, new(ChatRoomKind.Local, moved.SystemId))));
        Assert.All(retained, room => Assert.Equal(moved, Assert.Single(world.Members(connection, room))));
        Assert.Empty(world.Update(session, moved with { Name = "Renamed pilot" }));
        Assert.Equal("Renamed pilot", Assert.Single(world.Members(connection, new(ChatRoomKind.Local, moved.SystemId))).Name);
    }

    [Theory]
    [InlineData(ChatRoomKind.Local)]
    [InlineData(ChatRoomKind.Wormhole)]
    [InlineData(ChatRoomKind.Triglavian)]
    public void SystemChangesTransferLocalMembershipAndRosterWithoutAClientJoin(ChatRoomKind destinationKind)
    {
        ChatWorld world = new();
        Guid session = Guid.NewGuid(),
            observerSession = Guid.NewGuid(),
            connection = Guid.NewGuid(),
            observer = Guid.NewGuid();
        ChatPilot destinationPilot = Second with { SystemId = 30000144, LocalKind = destinationKind };
        ChatRoom destination = new(destinationKind, destinationPilot.SystemId);
        ChatRoom corporation = new(ChatRoomKind.Corporation, First.CorporationId);
        world.Update(session, First);
        world.Update(observerSession, destinationPilot);
        world.Connect(session, connection);
        world.Connect(observerSession, observer);
        world.Join(connection, Local, "source");
        world.Join(connection, corporation, "corporation");
        world.Join(observer, destination, "observer");
        ChatPilot moved = First with { SystemId = destinationPilot.SystemId, LocalKind = destinationKind };

        var arrival = world.Update(session, moved);

        Assert.Equal(new ChatEvent.Presence(Local, First, false), arrival[0].Event);
        Assert.Equal(new ChatEvent.Presence(destination, moved, true), arrival[1].Event);
        var roster = Assert.IsType<ChatEvent.Roster>(arrival[2].Event);
        Assert.Equal(connection, arrival[2].Recipient);
        Assert.Equal(destination, roster.Room);
        Assert.Equal(destinationKind == ChatRoomKind.Local ? new[] { First.Id, Second.Id } : [], roster.Pilots.Select(p => p.Id));
        Assert.Equal(moved, world.ConnectedPilot(connection));
        Assert.Empty(world.Update(session, moved));
        Assert.Empty(world.Leave(connection, Local));
        Assert.Throws<InvalidDataException>(() => world.Join(connection, Local, "stale"));
        Assert.Throws<InvalidDataException>(() => world.Send(connection, Local, "stale", "stale", 0));
        Assert.Equal(2, world.Send(connection, destination, "arrived", "arrival", 0).Count(e => e.Event is ChatEvent.Message));
        Assert.Single(world.Send(connection, corporation, "retained", "corp", TimeSpan.TicksPerSecond));
        Assert.DoesNotContain(world.Join(connection, destination, "retry"), e => e.Recipient == observer);

        var returned = world.Update(session, First);
        Assert.Contains(returned, e => e.Recipient == connection && e.Event is ChatEvent.Roster { Room: var room } && room == Local);
        Assert.Single(world.Members(connection, Local));
        Assert.Single(world.Members(observer, destination));
        Assert.Empty(world.Update(session, First));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstSystemMessageWaitsForJoinAndExpiresAtTheDeadline(bool atDeadline)
    {
        ChatWorld world = new();
        Guid session = Guid.NewGuid(),
            connection = Guid.NewGuid();
        world.Update(session, First);
        world.Connect(session, connection);
        Assert.Empty(world.Send(connection, Local, "first hello", "first-message", 0));
        Assert.Throws<InvalidDataException>(() => world.Send(connection, Local, "second", "second-message", 0));
        long delay = ChatRules.PendingMessageLifetime.Ticks - (atDeadline ? 0 : 1);
        var joined = world.Join(connection, Local, "join", delay);
        Assert.IsType<ChatEvent.Presence>(joined[0].Event);
        Assert.IsType<ChatEvent.Roster>(joined[1].Event);
        Assert.Equal(atDeadline ? 0 : 1, joined.Count(e => e.Event is ChatEvent.Message));
        Assert.DoesNotContain(world.Join(connection, Local, "retry", delay), e => e.Event is ChatEvent.Message);
    }

    [Fact]
    public void QueuedMessageUsesTheRateLimitOnceAndAnExplicitLeaveCancelsIt()
    {
        ChatWorld world = new();
        Guid session = Guid.NewGuid(),
            connection = Guid.NewGuid();
        ChatRoom corporation = new(ChatRoomKind.Corporation, First.CorporationId);
        world.Update(session, First);
        world.Connect(session, connection);
        world.Join(connection, corporation, "corp");
        world.Send(connection, Local, "queued", "first", 0);
        world.Send(connection, corporation, "second", "second", 0);
        world.Send(connection, corporation, "third", "third", 0);
        Assert.Single(world.Join(connection, Local, "local"), e => e.Event is ChatEvent.Message);
        Assert.Throws<InvalidDataException>(() => world.Send(connection, corporation, "fourth", "fourth", 0));
        world.Leave(connection, Local);
        world.Send(connection, Local, "canceled", "canceled", TimeSpan.TicksPerSecond);
        world.Leave(connection, Local);
        Assert.DoesNotContain(world.Join(connection, Local, "rejoin", TimeSpan.TicksPerSecond), e => e.Event is ChatEvent.Message);
    }

    [Fact]
    public void PendingMessageCannotEscapeToAnotherSystemOrSurviveDisconnect()
    {
        ChatWorld world = new();
        Guid session = Guid.NewGuid(),
            connection = Guid.NewGuid();
        world.Update(session, First);
        world.Connect(session, connection);
        Assert.Throws<InvalidDataException>(() => world.Send(connection, new(ChatRoomKind.Local, 30000144), "forged", "bad", 0));
        Assert.Empty(world.Send(connection, Local, "old hello", "old", 0));
        world.Update(session, First with { SystemId = 30000144 });
        world.Update(session, First);
        Assert.DoesNotContain(world.Join(connection, Local, "join"), e => e.Event is ChatEvent.Message);
        world.Leave(connection, Local);
        world.Send(connection, Local, "disconnected", "disconnect", TimeSpan.TicksPerSecond);
        world.Disconnect(connection);
        world.Connect(session, connection);
        Assert.DoesNotContain(world.Join(connection, Local, "reconnect"), e => e.Event is ChatEvent.Message);
    }

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
        Assert.Equal(2, world.Join(first, Local, "first").Count);
        var joined = world.Join(second, Local, "second");
        Assert.Equal(new[] { First.Id, Second.Id }, Assert.IsType<ChatEvent.Roster>(joined[1].Event).Pilots.Select(p => p.Id));
        Assert.Single(joined, e => e.Recipient == first && e.Event is ChatEvent.Presence { Available: true });
        Assert.Equal(2, world.Join(second, Local, "retry").Count);
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
        Assert.Equal(2, joined.Count);
        Assert.Equal(second, joined[0].Recipient);
        Assert.Empty(Assert.IsType<ChatEvent.Roster>(joined[1].Event).Pilots);
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
        ChatRuntime runtime = new(new LauncherOptions(), clock, new CooperativeFleetStore(clock));
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
        ChatRuntime runtime = new(new LauncherOptions(), new TestClock(), new CooperativeFleetStore(new TestClock()));
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
        ChatRuntime runtime = new(new LauncherOptions(), new TestClock(), new CooperativeFleetStore(new TestClock()));
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
