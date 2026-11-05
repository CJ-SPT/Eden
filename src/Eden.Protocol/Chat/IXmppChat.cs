using System.Threading.Channels;
using Eden.Gameplay.Chat;

namespace Eden.Protocol.Chat;

public sealed record XmppChatConnection(Guid Id, ChatPilot Pilot, ChannelReader<ChatDelivery> Events);

public interface IXmppChat
{
    ChatEndpoint Endpoint { get; }
    XmppChatConnection Authenticate(string username, string token);
    void Disconnect(Guid connection);
    void Join(Guid connection, ChatRoom room, string? requestId);
    void Leave(Guid connection, ChatRoom room);
    void Send(Guid connection, ChatRoom room, string text, string id);
    void Invite(Guid connection, ChatRoom room, long target);
    IReadOnlyList<ChatPilot> Members(Guid connection, ChatRoom room);
    ChatRoomInfo RoomInfo(Guid connection, ChatRoom room);
    IReadOnlyList<ChatRoomInfo> PublicRooms();
    ChatPilot? User(long pilotId);
}
