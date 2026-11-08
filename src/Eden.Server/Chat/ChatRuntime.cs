using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Fleets;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Chat;
using Eden.Server.Launcher;

namespace Eden.Server.Chat;

[Injectable(InjectionType.Singleton)]
internal sealed class ChatRuntime(LauncherOptions options, TimeProvider clock, ICooperativeFleets fleets) : IPilotChat, IXmppChat
{
    private const int QueueCapacity = 256;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();

    // Give transient rooms a fresh namespace after restart so a saved client
    // window cannot accidentally address another newly created conversation.
    private readonly ChatWorld _world = new(BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))) & (long.MaxValue / 2));
    private readonly Dictionary<string, PendingToken> _tokens = [];
    private readonly Dictionary<Guid, Channel<ChatDelivery>> _queues = [];

    private long _onlineAt;
    private TimeSpan _elapsed;

    internal (bool Active, TimeSpan Elapsed) HumanActivity()
    {
        lock (_gate)
        {
            return _world.HasHumanPilots ? (true, _elapsed + clock.GetElapsedTime(_onlineAt)) : (false, _elapsed);
        }
    }

    internal void PublishCapsuleer(ChatPilot pilot)
    {
        lock (_gate)
        {
            Dispatch(_world.UpdateCapsuleer(pilot));
        }
    }

    private sealed record PendingToken(Guid Session, long PilotId, DateTimeOffset Expires);

    public ChatEndpoint Endpoint { get; } = new(ChatServer.Domain, new Uri("tcp://" + options.GameAddress).DnsSafeHost);

    public void Update(Guid session, ChatPilot? pilot)
    {
        lock (_gate)
        {
            long? previousPilot = _world.Pilot(session)?.Id;
            bool active = _world.HasHumanPilots;
            Dispatch(_world.Update(session, pilot));
            if (active != _world.HasHumanPilots)
            {
                long stamp = clock.GetTimestamp();
                if (active)
                {
                    _elapsed += clock.GetElapsedTime(_onlineAt, stamp);
                }
                _onlineAt = stamp;
            }
            if (pilot is null || previousPilot != pilot.Id)
            {
                foreach (string token in _tokens.Where(t => t.Value.Session == session).Select(t => t.Key).ToArray())
                {
                    _tokens.Remove(token);
                }
            }
        }
    }

    public string IssueToken(Guid session)
    {
        lock (_gate)
        {
            if (_world.Pilot(session) is not { } pilot)
            {
                throw new InvalidDataException("Select a pilot before authenticating chat.");
            }
            foreach (
                string expired in _tokens
                    .Where(t => t.Value.Session == session || t.Value.Expires <= clock.GetUtcNow())
                    .Select(t => t.Key)
                    .ToArray()
            )
            {
                _tokens.Remove(expired);
            }
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _tokens.Add(Hash(token), new(session, pilot.Id, clock.GetUtcNow() + TokenLifetime));
            return token;
        }
    }

    public XmppChatConnection Authenticate(string username, string token)
    {
        lock (_gate)
        {
            if (
                token.Length > 128
                || !_tokens.TryGetValue(Hash(token), out var claim)
                || claim.Expires <= clock.GetUtcNow()
                || _world.Pilot(claim.Session) is not { } pilot
                || pilot.Id != claim.PilotId
                || username != pilot.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            )
            {
                throw new InvalidDataException("Chat authentication failed.");
            }
            _tokens.Remove(Hash(token));
            Guid id = Guid.NewGuid();
            var queue = Channel.CreateBounded<ChatDelivery>(
                new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }
            );
            _queues.Add(id, queue);
            Dispatch(_world.Connect(claim.Session, id));
            return new(id, pilot, queue.Reader);
        }
    }

    public void Disconnect(Guid connection)
    {
        lock (_gate)
        {
            Dispatch(_world.Disconnect(connection));
            if (_queues.Remove(connection, out var queue))
            {
                queue.Writer.TryComplete();
            }
        }
    }

    public void Join(Guid connection, ChatRoom room, string? requestId)
    {
        lock (_gate)
        {
            if (room.Kind == ChatRoomKind.Fleet && _world.ConnectedPilot(connection) is { } pilot)
            {
                Dispatch(_world.UpdateFleet(connection, fleets.Identity(pilot.Id)?.FleetId));
            }
            Dispatch(_world.Join(connection, room, requestId, clock.GetElapsedTime(0, clock.GetTimestamp()).Ticks));
        }
    }

    public void Leave(Guid connection, ChatRoom room)
    {
        lock (_gate)
        {
            Dispatch(_world.Leave(connection, room));
        }
    }

    public void Send(Guid connection, ChatRoom room, string text, string id)
    {
        lock (_gate)
        {
            if (room.Kind == ChatRoomKind.Fleet && _world.ConnectedPilot(connection) is { } pilot)
            {
                Dispatch(_world.UpdateFleet(connection, fleets.Identity(pilot.Id)?.FleetId));
            }
            Dispatch(_world.Send(connection, room, text, id, clock.GetElapsedTime(0, clock.GetTimestamp()).Ticks));
        }
    }

    public void Invite(Guid connection, ChatRoom room, long target)
    {
        lock (_gate)
        {
            Dispatch(_world.Invite(connection, room, target));
        }
    }

    public IReadOnlyList<ChatRoom> SystemChannels(Guid session)
    {
        lock (_gate)
        {
            return _world.SystemChannels(session);
        }
    }

    public ChatRoom CreatePrivate(Guid session, long target)
    {
        lock (_gate)
        {
            return _world.CreatePrivate(session, target);
        }
    }

    public ChatRoom CreatePublic(Guid session, string name)
    {
        lock (_gate)
        {
            return _world.CreatePublic(session, name);
        }
    }

    public void AllowPrivate(Guid session, ChatRoom room, long pilotId)
    {
        lock (_gate)
        {
            _world.AllowPrivate(session, room, pilotId);
        }
    }

    public IReadOnlyList<ChatPilot> Members(Guid connection, ChatRoom room)
    {
        lock (_gate)
        {
            if (room.Kind == ChatRoomKind.Fleet && _world.ConnectedPilot(connection) is { } pilot)
            {
                Dispatch(_world.UpdateFleet(connection, fleets.Identity(pilot.Id)?.FleetId));
            }
            return _world.Members(connection, room);
        }
    }

    public ChatRoomInfo RoomInfo(Guid connection, ChatRoom room)
    {
        lock (_gate)
        {
            if (room.Kind == ChatRoomKind.Fleet && _world.ConnectedPilot(connection) is { } pilot)
            {
                Dispatch(_world.UpdateFleet(connection, fleets.Identity(pilot.Id)?.FleetId));
            }
            return _world.RoomInfo(connection, room);
        }
    }

    public IReadOnlyList<ChatRoomInfo> PublicRooms()
    {
        lock (_gate)
        {
            return _world.PublicRooms();
        }
    }

    public ChatPilot? User(long pilotId)
    {
        lock (_gate)
        {
            return _world.OnlinePilot(pilotId);
        }
    }

    private void Dispatch(IReadOnlyList<ChatDelivery> deliveries)
    {
        Queue<ChatDelivery> pending = new(deliveries);
        while (pending.TryDequeue(out var delivery))
        {
            if (
                delivery.Event is ChatEvent.Message { Room.Kind: ChatRoomKind.Fleet } message
                && _world.ConnectedPilot(delivery.Recipient) is { } recipient
                && fleets.Identity(recipient.Id)?.FleetId != message.Room.Id
            )
            {
                foreach (var revoked in _world.UpdateFleet(delivery.Recipient, fleets.Identity(recipient.Id)?.FleetId))
                {
                    pending.Enqueue(revoked);
                }
                continue;
            }
            if (!_queues.TryGetValue(delivery.Recipient, out var queue))
            {
                continue;
            }
            bool accepted = queue.Writer.TryWrite(delivery);
            if (!accepted || delivery.Event is ChatEvent.Closed)
            {
                _queues.Remove(delivery.Recipient);
                queue.Writer.TryComplete();
                foreach (var departed in _world.Disconnect(delivery.Recipient))
                {
                    pending.Enqueue(departed);
                }
            }
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
