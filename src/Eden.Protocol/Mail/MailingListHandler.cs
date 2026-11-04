using Eden.Gameplay.Mail;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Mail;

[Injectable(InjectionType.Scoped)]
internal sealed class MailingListHandler(IPilotMailingLists lists, TimeProvider clock) : ISelectedPilotCallHandler
{
    private readonly Guid _origin = Guid.NewGuid();
    private long? _pilot;
    private long _cursor;
    private long _nextPoll;
    private HashSet<long> _known = [];
    public bool IsMonitoring => _pilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        if (_pilot != context.Pilot.PilotId)
        {
            _pilot = context.Pilot.PilotId;
            _cursor = lists.Observe(_pilot.Value);
            _known = lists.Joined(_pilot.Value).Select(l => l.Id).ToHashSet();
            _nextPoll = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        }
    }

    public void Reset()
    {
        _pilot = null;
        _known.Clear();
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (now < _nextPoll)
        {
            return;
        }
        var batch = lists.Changes(context.Pilot.PilotId, _cursor);
        _cursor = batch.Cursor;
        _nextPoll = batch.More ? now : checked(now + TimeSpan.TicksPerSecond);
        foreach (var change in batch.Items)
        {
            if (change.Kind == MailingListChangeKind.Role && _known.Contains(change.ListId))
            {
                // The role-changing RPC does not update the actor's native cache;
                // self-demotion therefore also needs the role notification.
                context.Notifications.Add(new MailingListRoleChanged(change.ListId, change.Role));
            }
            else if (change.Kind is MailingListChangeKind.Left or MailingListChangeKind.Deleted)
            {
                _known.Remove(change.ListId);
                if (change.Origin != _origin)
                {
                    context.Notifications.Add(
                        change.Kind == MailingListChangeKind.Left
                            ? new MailingListLeft(change.ListId, context.Pilot.PilotId)
                            : new MailingListDeleted(change.ListId)
                    );
                }
            }
            // The stock service has no external join callback. A fresh
            // GetJoinedLists/reconnect adopts memberships joined by another session.
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
        if (
            service != "mailingListsMgr"
            || method
                is not (
                    "GetJoinedLists"
                    or "GetInfo"
                    or "Create"
                    or "Join"
                    or "Leave"
                    or "Delete"
                    or "GetMembers"
                    or "KickMembers"
                    or "SetMembersMuted"
                    or "SetMembersOperator"
                    or "SetMembersClear"
                    or "GetSettings"
                    or "SetDefaultAccess"
                    or "SetEntitiesAccess"
                    or "ClearEntityAccess"
                    or "GetWelcomeMail"
                    or "SaveWelcomeMail"
                    or "SendWelcomeMail"
                    or "ClearWelcomeMail"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected mailing-list arguments.");
            }
            Enter(context);
            long pilot = context.Pilot.PilotId;
            if (method == "GetJoinedLists" && args.Length == 0)
            {
                var joined = lists.Joined(pilot);
                _known = joined.Select(l => l.Id).ToHashSet();
                return new BlueMap<long, MailingListReply>(joined.ToDictionary(l => l.Id, l => new MailingListReply(l)));
            }
            if (method == "Create" && args is [var name, long createAccess, long createRole, var createCost])
            {
                Free(createCost);
                long createdId = lists.Create(pilot, Text(name), Defaults(createAccess, createRole), _origin);
                _known.Add(createdId);
                return createdId;
            }
            if (method == "Join" && args is [var nameToJoin])
            {
                var joined = lists.Join(pilot, Text(nameToJoin), _origin);
                _known.Add(joined.Id);
                return new MailingListReply(joined);
            }
            if (args is not [long id, ..] || id <= 0)
            {
                throw new InvalidDataException("Choose a mailing list.");
            }
            switch (method, args)
            {
                case ("GetInfo", [_]):
                    return lists.Info(pilot, id) is { } info ? new MailingListReply(info) : PilotCallResult.None;
                case ("Leave", [_]):
                    lists.Leave(pilot, id, _origin);
                    _known.Remove(id);
                    break;
                case ("Delete", [_]):
                    lists.Delete(pilot, id, _origin);
                    _known.Remove(id);
                    break;
                case ("GetMembers", [_]):
                    return new BlueMap<long, int>(lists.Members(pilot, id).ToDictionary(m => m.Key, m => (int)m.Value));
                case ("KickMembers" or "SetMembersMuted" or "SetMembersOperator" or "SetMembersClear", [_, List<object?> selected]):
                    MailingListRole? target = method switch
                    {
                        "SetMembersMuted" => MailingListRole.Muted,
                        "SetMembersOperator" => MailingListRole.Operator,
                        "SetMembersClear" => MailingListRole.Member,
                        _ => null,
                    };
                    lists.ChangeMembers(pilot, id, selected.Select(Id).ToArray(), target, _origin);
                    break;
                case ("GetSettings", [_]):
                    return new MailingListSettingsReply(lists.Settings(pilot, id));
                case ("SetDefaultAccess", [_, long access, long role, var cost]):
                    Free(cost);
                    lists.SetDefaults(pilot, id, Defaults(access, role));
                    break;
                case ("SetEntitiesAccess", [_, Dictionary<long, object?> entries]):
                    lists.SetAccess(pilot, id, entries.ToDictionary(e => e.Key, e => Access(e.Value)), _origin);
                    break;
                case ("ClearEntityAccess", [_, long entity]) when entity > 0:
                    lists.ClearAccess(pilot, id, entity);
                    break;
                case ("GetWelcomeMail", [_]):
                    var welcome = lists.Welcome(pilot, id);
                    return new BlueList<MailingListWelcomeReply>(welcome is null ? [] : [new(welcome)]);
                case ("ClearWelcomeMail", [_]):
                    lists.SaveWelcome(pilot, id, null, false, _origin);
                    break;
                case ("SaveWelcomeMail" or "SendWelcomeMail", [_, var title, var body]):
                    lists.SaveWelcome(pilot, id, new(Text(title), Text(body)), method == "SendWelcomeMail", _origin);
                    break;
                default:
                    throw new InvalidDataException("Invalid mailing-list arguments.");
            }
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static long Id(object? value) =>
        value is long id && id > 0 ? id : throw new InvalidDataException("Choose a positive member ID.");

    private static string Text(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected mailing-list text.");

    private static MailingListAccess Access(object? value) =>
        value is long number && number is 0 or 1
            ? (MailingListAccess)number
            : throw new InvalidDataException("Choose allowed or blocked access.");

    private static MailingListDefaults Defaults(long access, long role) =>
        new(
            Access(access),
            role is 0 or 1 ? (MailingListRole)role : throw new InvalidDataException("Choose member or muted as the default role.")
        );

    private static void Free(object? value)
    {
        if (value is not (null or 0L or 0d))
        {
            throw new InvalidDataException("Paid mailing-list CSPA charges are not available.");
        }
    }
}
