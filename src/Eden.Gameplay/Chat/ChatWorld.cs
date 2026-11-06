namespace Eden.Gameplay.Chat;

// The host serializes operations and delivers the resulting immutable events.
// No network callback runs while these membership rules are being evaluated.
public sealed class ChatWorld(long roomSeed = 0)
{
    private readonly Dictionary<Guid, ChatPilot> _sessions = [];
    private readonly Dictionary<Guid, Guid> _connections = [];
    private readonly Dictionary<ChatRoom, HashSet<Guid>> _members = [];
    private readonly Dictionary<ChatRoom, HashSet<Guid>> _speakers = [];
    private readonly Dictionary<ChatRoom, ChatRoomInfo> _rooms = [];
    private readonly Dictionary<ChatRoom, HashSet<long>> _privateAccess = [];
    private readonly Dictionary<Guid, Queue<long>> _sentAt = [];
    private readonly Dictionary<Guid, PendingMessage> _pendingMessages = [];
    private long _nextRoom = roomSeed;

    private sealed record PendingMessage(ChatRoom Room, string Text, string Id, long QueuedAt);

    public ChatPilot? Pilot(Guid session) => _sessions.GetValueOrDefault(session);

    public ChatPilot? OnlinePilot(long id) => _sessions.Values.FirstOrDefault(p => p.Id == id);

    public ChatPilot? ConnectedPilot(Guid connection) => _connections.TryGetValue(connection, out var session) ? Pilot(session) : null;

    public IReadOnlyList<ChatDelivery> Update(Guid session, ChatPilot? pilot)
    {
        List<ChatDelivery> events = [];
        List<Guid> localTransfers = [];
        if (_sessions.GetValueOrDefault(session) == pilot)
        {
            return events;
        }
        foreach (var connection in _connections.Where(c => c.Value == session).Select(c => c.Key).ToArray())
        {
            if (_pendingMessages.TryGetValue(connection, out var pending) && (pilot is null || !Eligible(pilot, pending.Room)))
            {
                _pendingMessages.Remove(connection);
            }
            if (pilot is null || _sessions[session].Id != pilot.Id)
            {
                events.AddRange(Disconnect(connection));
                events.Add(new(connection, new ChatEvent.Closed()));
                continue;
            }
            foreach (var room in _members.Where(m => m.Value.Contains(connection) && !Eligible(pilot, m.Key)).Select(m => m.Key).ToArray())
            {
                if (room.Kind is ChatRoomKind.Local or ChatRoomKind.Wormhole or ChatRoomKind.Triglavian)
                {
                    localTransfers.Add(connection);
                }
                events.AddRange(Leave(connection, room));
            }
        }
        if (pilot is null)
        {
            _sessions.Remove(session);
        }
        else
        {
            _sessions[session] = pilot;
            // The native session-change callback leaves old Local, but does not
            // join its replacement. Transfer the existing membership after the
            // authoritative pilot update so presence and roster use the destination.
            foreach (var connection in localTransfers)
            {
                events.AddRange(Join(connection, new(pilot.LocalKind, pilot.SystemId), null));
            }
        }
        return events;
    }

    public IReadOnlyList<ChatDelivery> Connect(Guid session, Guid connection)
    {
        if (!_sessions.ContainsKey(session))
        {
            throw new InvalidDataException("Select a pilot before connecting to chat.");
        }
        List<ChatDelivery> events = [];
        foreach (var old in _connections.Where(c => c.Value == session).Select(c => c.Key).ToArray())
        {
            events.AddRange(Disconnect(old));
            events.Add(new(old, new ChatEvent.Closed()));
        }
        _connections.Add(connection, session);
        return events;
    }

    public IReadOnlyList<ChatRoom> SystemChannels(Guid session)
    {
        var pilot = Pilot(session) ?? throw new InvalidDataException("Select a pilot before joining chat.");
        return [new(pilot.LocalKind, pilot.SystemId), new(ChatRoomKind.Corporation, pilot.CorporationId)];
    }

    public ChatRoom CreatePrivate(Guid session, long target)
    {
        var pilot = Pilot(session) ?? throw new InvalidDataException("Select a pilot before creating chat.");
        CheckRoomLimit(pilot.Id);
        if (target == pilot.Id || !_sessions.Values.Any(p => p.Id == target))
        {
            throw new InvalidDataException("Choose another online pilot for a private conversation.");
        }
        ChatRoom room = new(ChatRoomKind.Private, ++_nextRoom);
        _rooms.Add(room, new(room, "Private chat", pilot.Id));
        _privateAccess.Add(room, [pilot.Id, target]);
        return room;
    }

    public ChatRoom CreatePublic(Guid session, string name)
    {
        var pilot = Pilot(session) ?? throw new InvalidDataException("Select a pilot before creating chat.");
        CheckRoomLimit(pilot.Id);
        name = name.Trim();
        if (name.Length is < 1 or > ChatRules.MaximumRoomNameLength || name.Any(char.IsControl))
        {
            throw new InvalidDataException("Choose a channel name between 1 and 60 characters.");
        }
        if (_rooms.Values.Any(r => r.Room.Kind == ChatRoomKind.Player && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("A channel with this name already exists.");
        }
        ChatRoom room = new(ChatRoomKind.Player, ++_nextRoom);
        _rooms.Add(room, new(room, name, pilot.Id));
        return room;
    }

    private void CheckRoomLimit(long pilotId)
    {
        if (_rooms.Values.Count(room => room.OwnerId == pilotId) >= ChatRules.MaximumCreatedRooms)
        {
            throw new InvalidDataException("Too many created channels.");
        }
    }

    public void AllowPrivate(Guid session, ChatRoom room, long target)
    {
        var pilot = Pilot(session);
        if (
            pilot is null
            || !_rooms.TryGetValue(room, out var info)
            || info.OwnerId != pilot.Id
            || !_privateAccess.TryGetValue(room, out var allowed)
        )
        {
            throw new InvalidDataException("Only the private channel's creator can invite pilots.");
        }
        if (!_sessions.Values.Any(p => p.Id == target))
        {
            throw new InvalidDataException("This pilot is not online.");
        }
        allowed.Add(target);
    }

    private bool Eligible(ChatPilot pilot, ChatRoom room) =>
        room.Kind switch
        {
            ChatRoomKind.Local or ChatRoomKind.Wormhole or ChatRoomKind.Triglavian => room.Id == pilot.SystemId
                && room.Kind == pilot.LocalKind,
            ChatRoomKind.Corporation => room.Id == pilot.CorporationId,
            ChatRoomKind.Private => _privateAccess.TryGetValue(room, out var allowed) && allowed.Contains(pilot.Id),
            ChatRoomKind.Player => _rooms.ContainsKey(room),
            _ => false,
        };

    private ChatPilot RequireMember(Guid connection, ChatRoom room)
    {
        var pilot = ConnectedPilot(connection);
        if (pilot is null || !Eligible(pilot, room) || !_members.TryGetValue(room, out var members) || !members.Contains(connection))
        {
            throw new InvalidDataException("Join an authorized channel before using it.");
        }
        return pilot;
    }

    public IReadOnlyList<ChatDelivery> Join(Guid connection, ChatRoom room, string? correlationId, long now = 0)
    {
        var pilot = ConnectedPilot(connection) ?? throw new InvalidDataException("Chat is not authenticated.");
        if (!Eligible(pilot, room))
        {
            throw new InvalidDataException("This pilot cannot join that channel.");
        }
        if (!_members.TryGetValue(room, out var members))
        {
            members = [];
            _members.Add(room, members);
        }
        bool joined = members.Contains(connection);
        if (!joined && _members.Count(m => m.Value.Contains(connection)) >= ChatRules.MaximumJoinedRooms)
        {
            throw new InvalidDataException("Too many joined channels.");
        }
        // The native client completes its join/window switch on this acknowledgement.
        // Deliver the roster afterwards, including on retries that repair a cleared UI.
        List<ChatDelivery> events = [new(connection, new ChatEvent.Presence(room, pilot, true), correlationId)];
        members.Add(connection);
        var visible = members
            .Where(member => !ChatRules.Delayed(room) || _speakers.GetValueOrDefault(room)?.Contains(member) == true)
            .Select(member => ConnectedPilot(member)!)
            .OrderBy(member => member.Id)
            .ToArray();
        if (room.Kind is ChatRoomKind.Local or ChatRoomKind.Wormhole or ChatRoomKind.Triglavian)
        {
            // Delayed Local ignores self-presence. Even an empty roster switches
            // its window to the destination without revealing silent pilots.
            events.Add(new(connection, new ChatEvent.Roster(room, visible)));
        }
        else
        {
            events.AddRange(
                visible
                    .Where(member => member.Id != pilot.Id)
                    .Select(member => new ChatDelivery(connection, new ChatEvent.Presence(room, member, true)))
            );
        }
        if (!joined && !ChatRules.Delayed(room))
        {
            events.AddRange(
                members
                    .Where(member => member != connection)
                    .Select(member => new ChatDelivery(member, new ChatEvent.Presence(room, pilot, true)))
            );
        }
        if (_pendingMessages.TryGetValue(connection, out var pending) && pending.Room == room)
        {
            _pendingMessages.Remove(connection);
            if (now - pending.QueuedAt < ChatRules.PendingMessageLifetime.Ticks)
            {
                events.AddRange(Broadcast(connection, room, pending.Text, pending.Id));
            }
        }
        return events;
    }

    public IReadOnlyList<ChatDelivery> Leave(Guid connection, ChatRoom room, bool disconnecting = false)
    {
        if (_pendingMessages.TryGetValue(connection, out var pending) && pending.Room == room)
        {
            _pendingMessages.Remove(connection);
        }
        if (!_members.TryGetValue(room, out var members) || !members.Contains(connection))
        {
            return [];
        }
        var pilot = ConnectedPilot(connection)!;
        bool visible = !ChatRules.Delayed(room) || _speakers.GetValueOrDefault(room)?.Contains(connection) == true;
        var events = members
            .Where(member => visible || member == connection)
            .Select(member => new ChatDelivery(member, new ChatEvent.Presence(room, pilot, false)))
            .ToArray();
        members.Remove(connection);
        _speakers.GetValueOrDefault(room)?.Remove(connection);
        if (members.Count == 0)
        {
            _members.Remove(room);
            _speakers.Remove(room);
            if (room.Kind == ChatRoomKind.Private && !disconnecting)
            {
                _rooms.Remove(room);
                _privateAccess.Remove(room);
            }
        }
        return events;
    }

    public IReadOnlyList<ChatDelivery> Disconnect(Guid connection)
    {
        List<ChatDelivery> events = [];
        foreach (var room in _members.Where(m => m.Value.Contains(connection)).Select(m => m.Key).ToArray())
        {
            events.AddRange(Leave(connection, room, disconnecting: true));
        }
        _connections.Remove(connection);
        _sentAt.Remove(connection);
        _pendingMessages.Remove(connection);
        return events;
    }

    public IReadOnlyList<ChatDelivery> Send(Guid connection, ChatRoom room, string text, string id, long now)
    {
        ChatRules.CheckMessage(text);
        if (!_members.TryGetValue(room, out var members) || !members.Contains(connection))
        {
            var selected = ConnectedPilot(connection);
            bool systemRoom =
                room.Kind is ChatRoomKind.Local or ChatRoomKind.Wormhole or ChatRoomKind.Triglavian or ChatRoomKind.Corporation;
            if (selected is null || !systemRoom || !Eligible(selected, room))
            {
                throw new InvalidDataException("Join an authorized channel before using it.");
            }
            if (
                _pendingMessages.TryGetValue(connection, out var pending)
                && now - pending.QueuedAt < ChatRules.PendingMessageLifetime.Ticks
            )
            {
                throw new InvalidDataException("Wait for the initial channel join before sending another message.");
            }
            CheckSendRate(connection, now);
            _pendingMessages[connection] = new(room, text, id, now);
            return [];
        }
        _ = RequireMember(connection, room);
        CheckSendRate(connection, now);
        return Broadcast(connection, room, text, id);
    }

    private void CheckSendRate(Guid connection, long now)
    {
        if (!_sentAt.TryGetValue(connection, out var times))
        {
            times = [];
            _sentAt.Add(connection, times);
        }
        while (times.TryPeek(out long first) && now - first >= TimeSpan.TicksPerSecond)
        {
            times.Dequeue();
        }
        if (times.Count >= ChatRules.MessagesPerSecond)
        {
            throw new InvalidDataException("Chat messages are being sent too quickly.");
        }
        times.Enqueue(now);
    }

    private IReadOnlyList<ChatDelivery> Broadcast(Guid connection, ChatRoom room, string text, string id)
    {
        var pilot = RequireMember(connection, room);
        List<ChatDelivery> events = [];
        if (ChatRules.Delayed(room))
        {
            if (!_speakers.TryGetValue(room, out var speakers))
            {
                speakers = [];
                _speakers.Add(room, speakers);
            }
            if (speakers.Add(connection))
            {
                events.AddRange(_members[room].Select(member => new ChatDelivery(member, new ChatEvent.Presence(room, pilot, true))));
            }
        }
        events.AddRange(_members[room].Select(member => new ChatDelivery(member, new ChatEvent.Message(room, pilot, text, id))));
        return events;
    }

    public IReadOnlyList<ChatDelivery> Invite(Guid connection, ChatRoom room, long target)
    {
        var pilot = RequireMember(connection, room);
        if (!_sessions.Values.Any(p => p.Id == target && Eligible(p, room)))
        {
            throw new InvalidDataException("This pilot cannot be invited to that channel.");
        }
        return _connections
            .Keys.Where(c => ConnectedPilot(c)?.Id == target)
            .Select(c => new ChatDelivery(c, new ChatEvent.Invitation(room, pilot)))
            .ToArray();
    }

    public IReadOnlyList<ChatPilot> Members(Guid connection, ChatRoom room)
    {
        _ = RequireMember(connection, room);
        return _members[room]
            .Where(member =>
                !ChatRules.Delayed(room) || member == connection || _speakers.GetValueOrDefault(room)?.Contains(member) == true
            )
            .Select(member => ConnectedPilot(member)!)
            .OrderBy(p => p.Id)
            .ToArray();
    }

    public ChatRoomInfo RoomInfo(Guid connection, ChatRoom room)
    {
        var pilot = ConnectedPilot(connection);
        if (pilot is null || !Eligible(pilot, room))
        {
            throw new InvalidDataException("This channel is unavailable.");
        }
        return _rooms.GetValueOrDefault(room) ?? new(room, room.Kind == ChatRoomKind.Corporation ? "Corporation" : "Local");
    }

    public IReadOnlyList<ChatRoomInfo> PublicRooms() => _rooms.Values.Where(r => r.Room.Kind == ChatRoomKind.Player).ToArray();
}
