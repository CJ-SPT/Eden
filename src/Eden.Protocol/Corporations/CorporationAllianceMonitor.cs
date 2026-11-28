using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Alliances;
using Eden.Protocol.Calendar;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Corporations;

internal sealed partial class CorporationHandler
{
    private long _allianceCursor;
    private readonly Dictionary<int, Alliance> _observedAlliances = [];
    private readonly Dictionary<int, int?> _observedCorporationAlliances = [];

    internal void ObserveCorporation(PlayerCorporation corporation) =>
        _observedCorporationAlliances[corporation.Id] = corporation.AllianceId;

    internal void ObserveAlliance(Alliance alliance) => _observedAlliances[alliance.Id] = alliance;

    private void DrainAllianceChanges(PilotCallContext context, int? currentAlliance)
    {
        int? previousAlliance = context.Pilot.Identity.AllianceId;
        long head = allianceMembership.Head;
        if (_observedPilot != context.Pilot.PilotId)
        {
            return;
        }
        if (previousAlliance != currentAlliance)
        {
            // The native cache retains a previously loaded foreign row after a session affiliation change.
            RefreshAffiliation(previousAlliance);
            RefreshAffiliation(currentAlliance);
        }
        if (head <= _allianceCursor)
        {
            return;
        }
        long previousCursor = _allianceCursor;
        foreach (var change in allianceMembership.Changes(context.Pilot.PilotId, previousCursor))
        {
            // A command can commit between the head read and the coherent feed read.
            // Acknowledge every observed record, including those newer than that head.
            _allianceCursor = Math.Max(_allianceCursor, change.Id);
            if ((change.Before is not null || change.After is not null) && !_observedAlliances.ContainsKey(change.AllianceId))
            {
                context.Notifications.Add(
                    new AllianceChanged(
                        change with
                        {
                            Before = change.Before?.ForViewer(previousAlliance),
                            After = change.After?.ForViewer(currentAlliance),
                        }
                    )
                );
            }
            if (change.MemberBefore != change.MemberAfter)
            {
                context.Notifications.Add(new AllianceMemberChanged(change));
            }
            if (change.ApplicationBefore is not null || change.ApplicationAfter is not null)
            {
                context.Notifications.Add(new AllianceApplicationChanged(change));
            }
            if (change.Calendar is { } calendar)
            {
                context.Notifications.Add(CalendarNotification.From(calendar));
            }
        }
        foreach (var after in allianceMembership.PublicChanges(_observedAlliances.Keys, previousCursor))
        {
            PublishPublic(after);
        }
        foreach (var (corporationId, before) in _observedCorporationAlliances)
        {
            int? after = alliances.Affiliation(corporationId);
            if (before != after)
            {
                _observedCorporationAlliances[corporationId] = after;
                context.Notifications.Add(new CorporationAllianceChanged(corporationId, before, after));
            }
        }
        _allianceCursor = Math.Max(_allianceCursor, head);

        void RefreshAffiliation(int? id)
        {
            if (id is int allianceId && _observedAlliances.ContainsKey(allianceId) && alliances.Read(allianceId) is { } after)
            {
                PublishPublic(after);
            }
        }

        void PublishPublic(Alliance state)
        {
            var after = state.ForViewer(currentAlliance);
            var before = _observedAlliances[after.Id];
            if (before != after)
            {
                _observedAlliances[after.Id] = after;
                context.Notifications.Add(new AllianceChanged(new(0, after.Id, 0, Before: before, After: after)));
            }
        }
    }
}

internal sealed record CorporationAllianceChanged(int CorporationId, int? Before, int? After) : PilotNotification("OnCorporationChanged")
{
    public override object?[] Arguments =>
        [
            CorporationId,
            new Dictionary<string, object?>
            {
                // The native cache treats all-None old or new values as insertion or removal.
                ["corporationID"] = new object?[] { CorporationId, CorporationId },
                ["allianceID"] = new object?[] { Before, After },
            },
        ];
}
