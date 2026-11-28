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
    private IReadOnlyList<CorporationWarNegotiation>? _negotiations;
    private (int Corporation, bool Eligible)? _permit;
    private bool _blocked;
    private DateTimeOffset _nextPoll;

    internal bool IsMonitoring => _wars is not null || _invitations is not null || _negotiations is not null || _permit is not null;

    internal void Reset()
    {
        _organization = 0;
        _wars = null;
        _invitations = null;
        _negotiations = null;
        _permit = null;
        _nextPoll = default;
    }

    internal void Watch(PilotCallContext context)
    {
        Select(context);
        _wars ??= wars.Wars(_organization).ToDictionary(war => war.Id);
    }

    internal void WatchPermit(int corporation, bool eligible)
    {
        if (_permit?.Corporation != corporation)
        {
            _permit = (corporation, eligible);
        }
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

    internal IReadOnlyList<CorporationWarNegotiation> WatchNegotiations(PilotCallContext context)
    {
        Select(context);
        var current = wars.Negotiations(context.Pilot.PilotId);
        _negotiations ??= current;
        return current;
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
        AdvancePermit(context);
    }

    private void AdvancePermit(PilotCallContext context)
    {
        if (_permit is not { } previous)
        {
            return;
        }
        if (previous.Corporation != context.Pilot.CorpId)
        {
            _permit = null;
            return;
        }

        bool eligible = wars.CorporationAllowsWar(previous.Corporation);
        if (previous.Eligible != eligible)
        {
            _permit = (previous.Corporation, eligible);
            context.Notifications.Add(new CorporationWarPermitChanged(previous.Corporation, eligible));
        }
    }

    internal void Advance(PilotCallContext context)
    {
        if (_wars is null && _negotiations is null)
        {
            return;
        }
        Select(context);
        HashSet<long> notified = [];
        if (_wars is { } previousWars)
        {
            var current = wars.Wars(_organization);
            foreach (var war in current)
            {
                var previous = previousWars.GetValueOrDefault(war.Id);
                if (!Equivalent(previous, war))
                {
                    context.Notifications.Add(new CorporationWarChanged(previous, war));
                    notified.Add(war.Id);
                }
            }
            if (notified.Count > 0 || previousWars.Count != current.Count)
            {
                var next = current.ToDictionary(war => war.Id);
                foreach (var previous in previousWars.Values)
                {
                    if (!next.ContainsKey(previous.Id))
                    {
                        context.Notifications.Add(new CorporationWarRemoved(previous));
                        notified.Add(previous.Id);
                    }
                }
                _wars = next;
            }
        }
        if (_negotiations is { } previousNegotiations)
        {
            var current = wars.Negotiations(context.Pilot.PilotId);
            if (!previousNegotiations.SequenceEqual(current))
            {
                _negotiations = current;
                RefreshNegotiationWars(context, previousNegotiations, current, notified);
            }
        }
    }

    private void RefreshNegotiationWars(
        PilotCallContext context,
        IReadOnlyList<CorporationWarNegotiation> before,
        IReadOnlyList<CorporationWarNegotiation> after,
        HashSet<long> notified
    )
    {
        // The native Wars panel refreshes negotiations when OnWarChanged reaches its local war service.
        foreach (long id in before.Except(after).Concat(after.Except(before)).Select(offer => offer.WarId).Distinct())
        {
            if (notified.Add(id) && wars.War(id) is { } war)
            {
                context.Notifications.Add(new CorporationWarChanged(war, war));
            }
        }
    }

    private static bool Equivalent(CorporationWar? previous, CorporationWar current) =>
        previous == current
        || previous is not null && previous == current with { Allies = previous.Allies } && previous.Allies.SequenceEqual(current.Allies);

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
            if (_negotiations is not null)
            {
                _negotiations = wars.Negotiations(context.Pilot.PilotId);
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
                .Where(entry => entry.Key != "allies" && !Equals(entry.Value, before.GetValueOrDefault(entry.Key)))
                .ToDictionary(entry => entry.Key, entry => (object?)new object?[] { before.GetValueOrDefault(entry.Key), entry.Value });
            IReadOnlyList<CorporationWarAlly> previousAllies = Previous?.Allies ?? [];
            if (!previousAllies.SequenceEqual(Current.Allies))
            {
                changes["allies"] = new object?[] { CorporationWarReply.AllyEntries(previousAllies), fields["allies"] };
            }
            return [new CorporationWarReply(Current), Owners(Current, previousAllies), changes];
        }
    }

    internal static BlueConstructor Owners(CorporationWar war, IReadOnlyList<CorporationWarAlly> previousAllies)
    {
        var owners = new long[] { war.AttackerId, war.DefenderId }
            .Concat(previousAllies.Concat(war.Allies).Select(ally => (long)ally.OwnerId))
            .Distinct()
            .Select(owner => (object?)owner)
            .ToList();
        return new BlueConstructor("__builtin__.set", [owners]);
    }
}

internal sealed record CorporationWarRemoved(CorporationWar Previous) : PilotNotification("OnWarChanged")
{
    // Build 3662168's evewar.util.HandleWarChange removes the cached war when the replacement ID is None.
    public override object?[] Arguments =>
        [
            new CorporationWarReply(Previous),
            CorporationWarChanged.Owners(Previous, []),
            new Dictionary<string, object?> { ["warID"] = new object?[] { Previous.Id, null } },
        ];
}

internal sealed record CorporationWarInvitationsUpdated() : PilotNotification("OnMutualWarsUpdated_Remote")
{
    public override object?[] Arguments => [];
}

internal sealed record CorporationWarPermitChanged(int CorporationId, bool Eligible) : PilotNotification("OnAllowWarUpdated")
{
    public override object?[] Arguments => [CorporationId, Eligible];
}
