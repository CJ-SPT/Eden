namespace Eden.Gameplay.Chat;

public enum ChatRoomKind
{
    Local,
    Wormhole,
    Triglavian,
    Corporation,
    Private,
    Player,
}

public sealed record ChatRoom(ChatRoomKind Kind, long Id);

public sealed record ChatPilot(
    long Id,
    string Name,
    int CorporationId,
    int SystemId,
    long Roles,
    int GenderId = 0,
    ChatRoomKind LocalKind = ChatRoomKind.Local
);

public sealed record ChatRoomInfo(ChatRoom Room, string Name, long? OwnerId = null);

public abstract record ChatEvent
{
    public sealed record Presence(ChatRoom Room, ChatPilot Pilot, bool Available) : ChatEvent;

    public sealed record Message(ChatRoom Room, ChatPilot Sender, string Text, string Id) : ChatEvent;

    public sealed record Invitation(ChatRoom Room, ChatPilot Sender) : ChatEvent;

    public sealed record Closed : ChatEvent;
}

public sealed record ChatDelivery(Guid Recipient, ChatEvent Event, string? CorrelationId = null);

public sealed record ChatEndpoint(string Domain, string Address);

public interface IPilotChat
{
    ChatEndpoint Endpoint { get; }
    void Update(Guid session, ChatPilot? pilot);
    string IssueToken(Guid session);
    IReadOnlyList<ChatRoom> SystemChannels(Guid session);
    ChatRoom CreatePrivate(Guid session, long otherPilot);
    ChatRoom CreatePublic(Guid session, string name);
    void AllowPrivate(Guid session, ChatRoom room, long pilotId);
}

public static class ChatRules
{
    public const int MaximumMessageLength = 1024;
    public const int MessagesPerSecond = 3;
    public const int MaximumRoomNameLength = 60;
    public const int MaximumJoinedRooms = 20;
    public const int MaximumCreatedRooms = 20;
    private const int FirstWormholeSystem = 31000000;
    private const int LastWormholeSystem = 31999999;
    private const int ZarzakhSystem = 30100000;
    private const int PochvenRegion = 10000070;

    public static ChatRoomKind LocalKind(int systemId, int regionId) =>
        systemId is >= FirstWormholeSystem and <= LastWormholeSystem or ZarzakhSystem ? ChatRoomKind.Wormhole
        : regionId == PochvenRegion ? ChatRoomKind.Triglavian
        : ChatRoomKind.Local;

    public static bool Delayed(ChatRoom room) => room.Kind is ChatRoomKind.Wormhole or ChatRoomKind.Triglavian;

    public static void CheckMessage(string text)
    {
        if (
            string.IsNullOrWhiteSpace(text)
            || text.Length > MaximumMessageLength
            || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t')
        )
        {
            throw new InvalidDataException("Chat messages must contain between 1 and 1024 characters without control codes.");
        }
    }
}
