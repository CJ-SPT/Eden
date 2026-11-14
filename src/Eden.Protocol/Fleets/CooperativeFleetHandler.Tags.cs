using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fleets;

internal sealed partial class CooperativeFleetHandler
{
    private IReadOnlyList<FleetTargetTag>? _sentTags;
    private long? _tagFleet;

    private static SpacePosition? TagPosition(PilotCallContext context, long item)
    {
        if (!context.Flight.CanUseSelfModules || context.Flight.InWarp)
        {
            return null;
        }
        if (item == context.Pilot.ShipId)
        {
            return context.Pilot.Position;
        }
        // Tagging uses the observer scene, not weapon lock eligibility or a fresh system contact query.
        return context
                .Flight.VisiblePeers.FirstOrDefault(peer =>
                    peer.Pilot.ShipId == item && peer.Warp is null && peer.DepartureGate is null && !peer.Arriving
                )
                ?.Pilot.Position
            ?? context.Flight.VisibleNpcs.FirstOrDefault(npc => npc.Id == item && npc.Warp is null)?.Position
            ?? context.Flight.VisibleDrones.FirstOrDefault(drone => drone.Id == item)?.Position
            ?? context.Flight.VisibleWrecks.FirstOrDefault(wreck => wreck.Id == item)?.Position
            ?? context.Flight.LocalObject(item)?.Position;
    }

    private void AdvanceTags(PilotCallContext context)
    {
        long? fleet = fleets.Identity(context.Pilot.PilotId)?.FleetId;
        var tags = fleets.TargetTags(context.Pilot.PilotId);
        var visible = tags.Where(tag =>
                tag.SystemId == context.Pilot.SystemId
                && TagPosition(context, tag.ItemId) is { } position
                && context.Pilot.Position!.DistanceTo(position) <= FleetRules.CommandRange
            )
            .ToArray();
        if (_tagFleet == fleet && _sentTags is not null && _sentTags.SequenceEqual(visible))
        {
            return;
        }
        bool notify = fleet is not null || _tagFleet is not null;
        _tagFleet = fleet;
        _sentTags = visible;
        if (notify)
        {
            context.Notifications.Add(new FleetStateNotification(visible));
        }
    }

    private PilotCallResult TagTarget(PilotCallContext context, object?[] args, Dictionary<string, object?>? keywords)
    {
        Advance(context);
        try
        {
            if (keywords is { Count: > 0 } || args is not [long item, var raw] || raw is not (null or string or byte[]))
            {
                throw new InvalidDataException("Choose a target and a fleet tag.");
            }
            string? tag = raw is null ? null : BlueMarshal.Text(raw);
            var identity = fleets.Identity(context.Pilot.PilotId) ?? throw new InvalidDataException("Join a fleet before tagging targets.");
            var position = TagPosition(context, item) ?? throw new InvalidDataException("Choose a visible object in space to tag.");
            fleets.TagTarget(context.Pilot.PilotId, identity.FleetId, new(item, context.Pilot.SystemId, position), tag);
            AdvanceTags(context);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
