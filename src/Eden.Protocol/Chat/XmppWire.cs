using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Eden.Gameplay.Chat;

namespace Eden.Protocol.Chat;

internal static class XmppWire
{
    internal const string StreamNamespace = "http://etherx.jabber.org/streams";
    internal const string ClientNamespace = "jabber:client";
    internal const string SaslNamespace = "urn:ietf:params:xml:ns:xmpp-sasl";
    internal const string BindNamespace = "urn:ietf:params:xml:ns:xmpp-bind";
    internal const string SessionNamespace = "urn:ietf:params:xml:ns:xmpp-session";
    internal const string MucUserNamespace = "http://jabber.org/protocol/muc#user";
    internal const string DiscoInfoNamespace = "http://jabber.org/protocol/disco#info";
    internal const string DiscoItemsNamespace = "http://jabber.org/protocol/disco#items";
    internal const string EveDataNamespace = "urn:xmpp:eve_user_data";
    internal const int CharacterType = 1373;

    internal static string Channel(ChatRoom room) =>
        (
            room.Kind switch
            {
                ChatRoomKind.Local => "local",
                ChatRoomKind.Wormhole => "wormhole",
                ChatRoomKind.Triglavian => "triglavian",
                ChatRoomKind.Corporation => "corp",
                ChatRoomKind.Fleet => "fleet",
                ChatRoomKind.Private => "private",
                ChatRoomKind.Player => "player",
                _ => throw new InvalidDataException("Unknown chat channel category."),
            }
        )
        + "_"
        + room.Id.ToString(CultureInfo.InvariantCulture);

    internal static ChatRoom ParseChannel(string value)
    {
        string[] parts = value.Split('_');
        if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
        {
            throw new InvalidDataException("Invalid chat channel.");
        }
        ChatRoomKind kind = parts[0] switch
        {
            "local" => ChatRoomKind.Local,
            "wormhole" => ChatRoomKind.Wormhole,
            "triglavian" => ChatRoomKind.Triglavian,
            "corp" => ChatRoomKind.Corporation,
            "fleet" => ChatRoomKind.Fleet,
            "private" => ChatRoomKind.Private,
            "player" => ChatRoomKind.Player,
            _ => throw new InvalidDataException("Unsupported chat channel category."),
        };
        return new(kind, id);
    }

    internal static ChatRoom ParseRoom(string jid, string domain)
    {
        string bare = jid.Split('/')[0];
        string suffix = "@conference." + domain;
        if (!bare.EndsWith(suffix, StringComparison.Ordinal) || bare.Length <= suffix.Length)
        {
            throw new InvalidDataException("The chat room belongs to another server.");
        }
        return ParseChannel(bare[..^suffix.Length]);
    }

    internal static long ParseUser(string jid, string domain)
    {
        string bare = jid.Split('/')[0];
        string suffix = "@" + domain;
        if (
            !bare.EndsWith(suffix, StringComparison.Ordinal)
            || !long.TryParse(bare[..^suffix.Length], NumberStyles.None, CultureInfo.InvariantCulture, out long id)
            || id <= 0
        )
        {
            throw new InvalidDataException("Invalid chat user address.");
        }
        return id;
    }

    internal static string UserJid(long pilot, string domain) => pilot.ToString(CultureInfo.InvariantCulture) + "@" + domain;

    internal static string RoomJid(ChatRoom room, string domain) => Channel(room) + "@conference." + domain;

    internal static string Xml(XElement element) => element.ToString(SaveOptions.DisableFormatting);

    internal static XElement Availability(string boundJid) =>
        new("presence", new XAttribute("from", boundJid), new XAttribute("to", boundJid));

    internal static XElement UserData(ChatPilot pilot) =>
        new(
            "eve_user_data",
            new XAttribute("corpid", pilot.CorporationId),
            new XAttribute("allianceid", 0),
            new XAttribute("warfactionid", 0),
            new XAttribute("typeid", CharacterType),
            new XAttribute("role", pilot.Roles),
            // cfg.eveowners.Hint expects the native owner row's positional data.
            new XAttribute("info", JsonSerializer.Serialize(new object?[] { pilot.Id, pilot.Name, CharacterType, pilot.GenderId, null }))
        );

    private sealed record NativeUserData(
        [property: JsonPropertyName("corpid")] int CorporationId,
        [property: JsonPropertyName("allianceid")] int AllianceId,
        [property: JsonPropertyName("warfactionid")] int WarFactionId,
        [property: JsonPropertyName("typeid")] int TypeId,
        [property: JsonPropertyName("role")] long Roles,
        [property: JsonPropertyName("info")] string Info
    );

    private sealed record LocalControl(
        [property: JsonPropertyName("cmd")] string Command,
        [property: JsonPropertyName("charid")] long CharacterId,
        [property: JsonPropertyName("userdata")] NativeUserData UserData
    );

    internal static XElement Event(ChatDelivery delivery, string domain, string recipient) =>
        delivery.Event switch
        {
            ChatEvent.Presence presence => Presence(delivery, presence, domain, recipient),
            ChatEvent.Roster roster => new XElement(
                "presence",
                new XAttribute("from", RoomJid(roster.Room, domain) + "/viking"),
                new XAttribute("to", recipient),
                roster.Pilots.Select(UserData)
            ),
            ChatEvent.Message message => new XElement(
                "message",
                new XAttribute("from", RoomJid(message.Room, domain) + "/" + message.Sender.Id),
                new XAttribute("to", recipient),
                new XAttribute("type", "groupchat"),
                new XAttribute("id", message.Id),
                new XElement("body", message.Text)
            ),
            ChatEvent.Invitation invite => new XElement(
                "message",
                new XAttribute("from", RoomJid(invite.Room, domain)),
                new XAttribute("to", recipient),
                new XElement(
                    XName.Get("x", MucUserNamespace),
                    new XElement(
                        XName.Get("invite", MucUserNamespace),
                        new XAttribute("from", UserJid(invite.Sender.Id, domain)),
                        new XElement(XName.Get("reason", MucUserNamespace), "Private chat")
                    )
                )
            ),
            _ => throw new InvalidDataException("Unknown chat delivery."),
        };

    private static XElement Presence(ChatDelivery delivery, ChatEvent.Presence presence, string domain, string recipient)
    {
        XNamespace muc = MucUserNamespace;
        bool self = recipient.StartsWith(UserJid(presence.Pilot.Id, domain) + "/", StringComparison.Ordinal);
        if (presence.Room.Kind == ChatRoomKind.Local && !self)
        {
            // Build 3662168 explicitly ignores ordinary peers' Local leave
            // presence. _HandleMessageFromAdmin owns Local join/leave updates.
            var pilot = presence.Pilot;
            LocalControl command = new(
                presence.Available ? "join" : "leave",
                pilot.Id,
                new(
                    pilot.CorporationId,
                    0,
                    0,
                    CharacterType,
                    pilot.Roles,
                    JsonSerializer.Serialize(new object?[] { pilot.Id, pilot.Name, CharacterType, pilot.GenderId, null })
                )
            );
            return new(
                "message",
                new XAttribute("from", RoomJid(presence.Room, domain) + "/admin"),
                new XAttribute("to", recipient),
                new XAttribute("type", "groupchat"),
                new XElement("body", JsonSerializer.Serialize(command))
            );
        }
        return new(
            "presence",
            new XAttribute("from", RoomJid(presence.Room, domain) + "/" + presence.Pilot.Id),
            new XAttribute("to", recipient),
            delivery.CorrelationId is null ? null : new XAttribute("id", delivery.CorrelationId),
            presence.Available ? null : new XAttribute("type", "unavailable"),
            new XElement(
                muc + "x",
                new XElement(
                    muc + "item",
                    new XAttribute("affiliation", "member"),
                    new XAttribute("role", presence.Available ? "participant" : "none"),
                    new XAttribute("jid", UserJid(presence.Pilot.Id, domain)),
                    null
                ),
                self ? new XElement(muc + "status", new XAttribute("code", "110")) : null
            ),
            UserData(presence.Pilot)
        );
    }

    internal static XElement Result(XElement request, XElement? payload = null) =>
        new(
            "iq",
            new XAttribute("type", "result"),
            new XAttribute("id", (string?)request.Attribute("id") ?? ""),
            request.Attribute("to") is { } to ? new XAttribute("from", to.Value) : null,
            payload
        );

    internal static XElement Error(XElement request, string message, string condition = "not-allowed") =>
        new(
            request.Name.LocalName,
            new XAttribute("type", "error"),
            request.Attribute("id") is { } id ? new XAttribute("id", id.Value) : null,
            request.Attribute("to") is { } to ? new XAttribute("from", to.Value) : null,
            new XElement(
                "error",
                new XAttribute("type", "cancel"),
                new XAttribute("code", "403"),
                new XElement(XName.Get(condition, "urn:ietf:params:xml:ns:xmpp-stanzas")),
                new XElement(XName.Get("text", "urn:ietf:params:xml:ns:xmpp-stanzas"), message)
            )
        );
}
