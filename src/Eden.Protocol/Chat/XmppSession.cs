using System.Text;
using System.Xml.Linq;
using Eden.Gameplay.Chat;

namespace Eden.Protocol.Chat;

[Injectable]
public sealed class XmppSession(IXmppChat chat)
{
    public async Task RunAsync(Stream stream, CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lifetime.CancelAfter(TimeSpan.FromSeconds(30));
        using XmppStreamReader reader = new(stream);
        using SemaphoreSlim output = new(1);
        XmppChatConnection? connection = null;
        Task? events = null;
        string domain = chat.Endpoint.Domain;
        string? jid = null;
        bool opened = false;
        bool ready = false;
        async Task WriteAsync(string xml)
        {
            await output.WaitAsync(lifetime.Token);
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(xml), lifetime.Token);
                await stream.FlushAsync(lifetime.Token);
            }
            finally
            {
                output.Release();
            }
        }
        async Task DeliverAsync(XmppChatConnection active)
        {
            try
            {
                await foreach (var delivery in active.Events.ReadAllAsync(lifetime.Token))
                {
                    if (delivery.Event is ChatEvent.Closed)
                    {
                        await WriteAsync("</stream:stream>");
                        break;
                    }
                    await WriteAsync(XmppWire.Xml(XmppWire.Event(delivery, domain, jid!)));
                    if (delivery is { CorrelationId: not null, Event: ChatEvent.Presence { Available: true } presence })
                    {
                        await WriteAsync(
                            XmppWire.Xml(
                                new XElement(
                                    "message",
                                    new XAttribute("from", XmppWire.RoomJid(presence.Room, domain)),
                                    new XAttribute("type", "groupchat"),
                                    new XElement("subject")
                                )
                            )
                        );
                    }
                }
            }
            finally
            {
                await lifetime.CancelAsync();
            }
        }
        try
        {
            while (await reader.ReadAsync(lifetime.Token) is { } stanza)
            {
                if (stanza.Name == XName.Get("stream", XmppWire.StreamNamespace))
                {
                    if ((string?)stanza.Attribute("to") != domain || opened || ready)
                    {
                        throw new InvalidDataException("Invalid XMPP stream destination or restart.");
                    }
                    opened = true;
                    await WriteAsync(
                        "<stream:stream xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' from='"
                            + domain
                            + "' id='"
                            + Guid.NewGuid().ToString("N")
                            + "' version='1.0'>"
                    );
                    await WriteAsync(
                        connection is null
                            ? "<stream:features><mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'><mechanism>PLAIN</mechanism>"
                                + "</mechanisms></stream:features>"
                            : "<stream:features><bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'/><session "
                                + "xmlns='urn:ietf:params:xml:ns:xmpp-session'/></stream:features>"
                    );
                    continue;
                }
                if (!opened)
                {
                    throw new InvalidDataException("Open an XMPP stream first.");
                }
                if (connection is null)
                {
                    try
                    {
                        if (stanza.Name != XName.Get("auth", XmppWire.SaslNamespace) || (string?)stanza.Attribute("mechanism") != "PLAIN")
                        {
                            throw new InvalidDataException("Unsupported chat authentication.");
                        }
                        string[] fields = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(stanza.Value)).Split('\0');
                        if (fields.Length != 3 || (fields[0].Length > 0 && fields[0] != fields[1]))
                        {
                            throw new InvalidDataException("Invalid chat authentication identity.");
                        }
                        connection = chat.Authenticate(fields[1], fields[2]);
                    }
                    catch (Exception error) when (error is InvalidDataException or FormatException or DecoderFallbackException)
                    {
                        await WriteAsync("<failure xmlns='urn:ietf:params:xml:ns:xmpp-sasl'><not-authorized/></failure>");
                        return;
                    }
                    await WriteAsync("<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'/>");
                    opened = false;
                    continue;
                }
                try
                {
                    var payload = stanza.Elements().FirstOrDefault();
                    if (stanza.Name.LocalName == "iq" && payload?.Name == XName.Get("bind", XmppWire.BindNamespace) && jid is null)
                    {
                        string resource = payload.Elements().FirstOrDefault(e => e.Name.LocalName == "resource")?.Value ?? "eveclient";
                        if (resource.Length is < 1 or > 128 || resource.Any(char.IsControl))
                        {
                            throw new InvalidDataException("Invalid chat resource.");
                        }
                        jid = XmppWire.UserJid(connection.Pilot.Id, domain) + "/" + resource;
                        await WriteAsync(
                            XmppWire.Xml(
                                XmppWire.Result(
                                    stanza,
                                    new XElement(
                                        XName.Get("bind", XmppWire.BindNamespace),
                                        new XElement(XName.Get("jid", XmppWire.BindNamespace), jid)
                                    )
                                )
                            )
                        );
                        continue;
                    }
                    if (
                        stanza.Name.LocalName == "iq"
                        && payload?.Name == XName.Get("session", XmppWire.SessionNamespace)
                        && jid is not null
                        && !ready
                    )
                    {
                        await WriteAsync(XmppWire.Xml(XmppWire.Result(stanza)));
                        ready = true;
                        lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                        events = DeliverAsync(connection);
                        continue;
                    }
                    if (!ready)
                    {
                        throw new InvalidDataException("Bind and establish the chat session first.");
                    }
                    string to = (string?)stanza.Attribute("to") ?? "";
                    switch (stanza.Name.LocalName)
                    {
                        case "presence":
                            if (to.Length > 0)
                            {
                                var room = XmppWire.ParseRoom(to, domain);
                                string nick = to[(to.IndexOf('/') + 1)..];
                                if (nick != connection.Pilot.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                {
                                    throw new InvalidDataException("Chat nicknames must match the selected pilot.");
                                }
                                if ((string?)stanza.Attribute("type") == "unavailable")
                                {
                                    chat.Leave(connection.Id, room);
                                }
                                else
                                {
                                    chat.Join(connection.Id, room, (string?)stanza.Attribute("id"));
                                }
                            }
                            break;
                        case "message":
                            var messageRoom = XmppWire.ParseRoom(to, domain);
                            var body = stanza.Elements().FirstOrDefault(e => e.Name.LocalName == "body");
                            if (body is not null && (string?)stanza.Attribute("type") == "groupchat")
                            {
                                chat.Send(
                                    connection.Id,
                                    messageRoom,
                                    body.Value,
                                    (string?)stanza.Attribute("id") ?? Guid.NewGuid().ToString("N")
                                );
                            }
                            else if (stanza.Descendants(XName.Get("invite", XmppWire.MucUserNamespace)).FirstOrDefault() is { } invite)
                            {
                                chat.Invite(connection.Id, messageRoom, XmppWire.ParseUser((string?)invite.Attribute("to") ?? "", domain));
                            }
                            else if (!stanza.Descendants().Any(e => e.Name.LocalName == "decline"))
                            {
                                throw new InvalidDataException("Unsupported chat message.");
                            }
                            break;
                        case "iq":
                            await WriteAsync(XmppWire.Xml(HandleIq(connection, stanza, to, payload)));
                            break;
                        default:
                            throw new InvalidDataException("Unsupported chat stanza.");
                    }
                }
                catch (InvalidDataException error)
                {
                    await WriteAsync(XmppWire.Xml(XmppWire.Error(stanza, error.Message)));
                }
            }
        }
        finally
        {
            if (connection is not null)
            {
                chat.Disconnect(connection.Id);
            }
            await lifetime.CancelAsync();
            if (events is not null)
            {
                try
                {
                    await events;
                }
                catch (OperationCanceledException) { }
            }
        }
    }

    private XElement HandleIq(XmppChatConnection connection, XElement request, string to, XElement? payload)
    {
        if (payload is null)
        {
            return XmppWire.Error(request, "Missing IQ payload.", "bad-request");
        }
        string ns = payload.Name.NamespaceName;
        if (payload.Name == XName.Get("ping", "urn:xmpp:ping") || payload.Name == XName.Get("query", "jabber:iq:roster"))
        {
            return XmppWire.Result(request, payload.Name.LocalName == "query" ? new XElement(payload.Name) : null);
        }
        if (ns == XmppWire.EveDataNamespace && (string?)request.Attribute("type") == "get")
        {
            var user =
                chat.User(XmppWire.ParseUser((string?)payload.Attribute("jid") ?? "", chat.Endpoint.Domain))
                ?? throw new InvalidDataException("This chat user is offline.");
            return XmppWire.Result(request, new XElement(payload.Name, XmppWire.UserData(user)));
        }
        if ((string?)request.Attribute("type") != "get")
        {
            return XmppWire.Error(request, "Chat metadata is server-owned.");
        }
        if (ns == XmppWire.DiscoItemsNamespace && to == "conference." + chat.Endpoint.Domain)
        {
            return XmppWire.Result(
                request,
                new XElement(
                    payload.Name,
                    chat.PublicRooms()
                        .Select(room => new XElement(
                            XName.Get("item", ns),
                            new XAttribute("jid", XmppWire.RoomJid(room.Room, chat.Endpoint.Domain)),
                            new XAttribute("name", room.Name)
                        ))
                )
            );
        }
        var target =
            to == "conference." + chat.Endpoint.Domain && payload.Attribute("node") is { } node
                ? XmppWire.ParseChannel(node.Value)
                : XmppWire.ParseRoom(to, chat.Endpoint.Domain);
        var info = chat.RoomInfo(connection.Id, target);
        if (ns == XmppWire.DiscoInfoNamespace)
        {
            XNamespace data = "jabber:x:data";
            int occupants;
            try
            {
                occupants = chat.Members(connection.Id, target).Count;
            }
            catch (InvalidDataException)
            {
                occupants = 0;
            }
            return XmppWire.Result(
                request,
                new XElement(
                    payload.Name,
                    new XElement(
                        XName.Get("identity", ns),
                        new XAttribute("category", "conference"),
                        new XAttribute("type", "text"),
                        new XAttribute("name", info.Name)
                    ),
                    new XElement(XName.Get("feature", ns), new XAttribute("var", "http://jabber.org/protocol/muc")),
                    new XElement(
                        data + "x",
                        new XAttribute("type", "result"),
                        new XElement(
                            data + "field",
                            new XAttribute("var", "muc#roominfo_occupants"),
                            new XElement(data + "value", occupants)
                        )
                    )
                )
            );
        }
        if (ns == "http://jabber.org/protocol/muc#owner")
        {
            XNamespace data = "jabber:x:data";
            return XmppWire.Result(
                request,
                new XElement(
                    payload.Name,
                    new XElement(
                        data + "x",
                        new XAttribute("type", "form"),
                        new XElement(
                            data + "field",
                            new XAttribute("var", "muc#roomconfig_roomname"),
                            new XElement(data + "value", info.Name)
                        ),
                        new XElement(data + "field", new XAttribute("var", "muc#roomconfig_roomdesc"), new XElement(data + "value", ""))
                    )
                )
            );
        }
        if (ns is XmppWire.DiscoItemsNamespace or "http://jabber.org/protocol/muc#admin")
        {
            return XmppWire.Result(
                request,
                new XElement(
                    payload.Name,
                    chat.Members(connection.Id, target)
                        .Select(member => new XElement(
                            XName.Get("item", ns),
                            new XAttribute("jid", XmppWire.UserJid(member.Id, chat.Endpoint.Domain)),
                            new XAttribute("nick", member.Id),
                            new XAttribute("role", "participant"),
                            new XAttribute("affiliation", "member"),
                            XmppWire.UserData(member)
                        ))
                )
            );
        }
        return XmppWire.Error(request, "Unsupported chat query.", "feature-not-implemented");
    }
}
