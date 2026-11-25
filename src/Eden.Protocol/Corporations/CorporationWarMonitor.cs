using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable(InjectionType.Scoped)]
internal sealed class CorporationWarMonitor(ICorporationWars wars, TimeProvider clock)
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(1);
    private int _organization;
    private Dictionary<long, CorporationWar>? _wars;
    private IReadOnlyList<CorporationWarInvitation>? _invitations;
    private bool _blocked;
    private DateTimeOffset _nextPoll;

    internal bool IsMonitoring => _wars is not null || _invitations is not null;

    internal void Reset()
    {
        _organization = 0;
        _wars = null;
        _invitations = null;
        _nextPoll = default;
    }

    internal void Watch(PilotCallContext context)
    {
        Select(context);
        _wars ??= wars.Wars(_organization).ToDictionary(war => war.Id);
    }

    internal void WatchInvitations(PilotCallContext context)
    {
        Select(context);
        if (_invitations is null)
        {
            _invitations = wars.Invitations(context.Pilot.PilotId);
            _blocked = wars.InvitationsBlocked(context.Pilot.PilotId);
        }
    }

    internal void Poll(PilotCallContext context)
    {
        var now = clock.GetUtcNow();
        if (!IsMonitoring || now < _nextPoll)
        {
            return;
        }
        _nextPoll = now + s_pollInterval;
        Advance(context);
        AdvanceInvitations(context);
    }

    internal void Advance(PilotCallContext context)
    {
        if (_wars is null)
        {
            return;
        }
        Watch(context);
        var current = wars.Wars(_organization);
        var changes = current.Where(war => war != _wars!.GetValueOrDefault(war.Id)).ToArray();
        if (changes.Length == 0 && _wars!.Count == current.Count)
        {
            return;
        }
        foreach (var war in changes)
        {
            context.Notifications.Add(new CorporationWarChanged(_wars.GetValueOrDefault(war.Id), war));
        }
        _wars = current.ToDictionary(war => war.Id);
    }

    internal void AdvanceInvitations(PilotCallContext context)
    {
        if (_invitations is null)
        {
            return;
        }
        WatchInvitations(context);
        var invitations = wars.Invitations(context.Pilot.PilotId);
        bool blocked = wars.InvitationsBlocked(context.Pilot.PilotId);
        if (_blocked == blocked && invitations.SequenceEqual(_invitations!))
        {
            return;
        }
        _invitations = invitations;
        _blocked = blocked;
        context.Notifications.Add(new CorporationWarInvitationsUpdated());
    }

    private void Select(PilotCallContext context)
    {
        int organization = wars.Organization(context.Pilot.PilotId);
        if (_organization != organization)
        {
            _organization = organization;
            if (_wars is not null)
            {
                _wars = wars.Wars(_organization).ToDictionary(war => war.Id);
            }
            if (_invitations is not null)
            {
                _invitations = wars.Invitations(context.Pilot.PilotId);
                _blocked = wars.InvitationsBlocked(context.Pilot.PilotId);
            }
        }
    }
}

internal sealed record CorporationWarChanged(CorporationWar? Previous, CorporationWar Current) : PilotNotification("OnWarChanged")
{
    public override object?[] Arguments
    {
        get
        {
            var before = Previous is { } previous ? CorporationWarReply.Fields(previous) : [];
            var fields = CorporationWarReply.Fields(Current);
            var changes = fields
                .Where(entry => !Equals(entry.Value, before.GetValueOrDefault(entry.Key)))
                .ToDictionary(entry => entry.Key, entry => (object?)new object?[] { before.GetValueOrDefault(entry.Key), entry.Value });
            return
            [
                new CorporationWarReply(Current),
                new BlueConstructor("__builtin__.set", [new List<object?> { Current.AttackerId, Current.DefenderId }]),
                changes,
            ];
        }
    }
}

internal sealed record CorporationWarInvitationsUpdated() : PilotNotification("OnMutualWarsUpdated_Remote")
{
    public override object?[] Arguments => [];
}
