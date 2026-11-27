using Eden.Gameplay.Corporations;
using Eden.Protocol.Alliances;
using Eden.Protocol.Calendar;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Corporations;

internal sealed partial class CorporationHandler
{
    private long _allianceCursor;
    private readonly Dictionary<int, int?> _observedCorporationAlliances = [];

    internal void ObserveCorporation(PlayerCorporation corporation) =>
        _observedCorporationAlliances[corporation.Id] = corporation.AllianceId;

    private void DrainAllianceChanges(PilotCallContext context)
    {
        long head = allianceMembership.Head;
        if (_observedPilot != context.Pilot.PilotId || head <= _allianceCursor)
        {
            return;
        }
        foreach (var change in allianceMembership.Changes(context.Pilot.PilotId, _allianceCursor))
        {
            // A command can commit between the head read and the coherent feed read.
            // Acknowledge every observed record, including those newer than that head.
            _allianceCursor = Math.Max(_allianceCursor, change.Id);
            if (change.Before is not null || change.After is not null)
            {
                context.Notifications.Add(new AllianceChanged(change));
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
