using Eden.Gameplay.Chat;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Chat;

[Injectable(InjectionType.Scoped)]
internal sealed class ChatHandler(IPilotChat chat) : ISelectedPilotCallHandler, IDisposable
{
    private readonly Guid _session = Guid.NewGuid();
    private ChatPilot? _pilot;

    public void Advance(PilotCallContext context)
    {
        var selected = context.ActiveFlight?.Pilot;
        ChatPilot? pilot = selected is null
            ? null
            : new(
                selected.PilotId,
                selected.Name,
                selected.CorpId,
                selected.SystemId,
                context.Roles,
                selected.Profile.GenderId,
                ChatRules.LocalKind(selected.SystemId, selected.RegionId)
            );
        if (_pilot != pilot)
        {
            chat.Update(_session, pilot);
            _pilot = pilot;
        }
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "XmppChatMgr" && (service != "chatAuthenticationService" || method != "GetAuthenticationToken"))
        {
            return PilotCallResult.Unsupported;
        }
        Advance(context);
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected chat arguments.");
        }
        try
        {
            return (service, method, args) switch
            {
                ("chatAuthenticationService", "GetAuthenticationToken", []) => chat.IssueToken(_session),
                ("XmppChatMgr", "Hostname", []) => chat.Endpoint.Domain,
                ("XmppChatMgr", "GetDeprecatedPrefsFallback", []) => chat.Endpoint.Address,
                ("XmppChatMgr", "ResyncSystemChannelAccess", []) => new ChatChannelList(chat.SystemChannels(_session)),
                ("XmppChatMgr", "CreatePrivateChannel", [long id]) when id > 0 => XmppWire.Channel(chat.CreatePrivate(_session, id)),
                ("XmppChatMgr", "CreatePlayerOwnedChannel", [var name]) when name is string or byte[] => XmppWire.Channel(
                    chat.CreatePublic(_session, BlueMarshal.Text(name))
                ),
                ("XmppChatMgr", "AddCharacterToChannel", [var channel, long id, ..]) when channel is string or byte[] && id > 0 => Allow(
                    BlueMarshal.Text(channel),
                    id
                ),
                (
                    "XmppChatMgr",
                    "Hostname"
                        or "GetDeprecatedPrefsFallback"
                        or "ResyncSystemChannelAccess"
                        or "CreatePrivateChannel"
                        or "CreatePlayerOwnedChannel"
                        or "AddCharacterToChannel",
                    _
                ) => new PilotCallResult.Failure("Invalid chat request."),
                ("chatAuthenticationService", "GetAuthenticationToken", _) => new PilotCallResult.Failure("Invalid chat token request."),
                _ => PilotCallResult.Unsupported,
            };
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private PilotCallResult Allow(string name, long pilot)
    {
        chat.AllowPrivate(_session, XmppWire.ParseChannel(name), pilot);
        return PilotCallResult.None;
    }

    public void Dispose()
    {
        chat.Update(_session, null);
        _pilot = null;
    }
}

internal sealed record ChatChannelList(IReadOnlyList<ChatRoom> Channels) : BlueModel
{
    public override object ToBlue() => Channels.Select(room => (object?)XmppWire.Channel(room)).ToList();
}
