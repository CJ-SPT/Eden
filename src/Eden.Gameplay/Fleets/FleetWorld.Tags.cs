namespace Eden.Gameplay.Fleets;

public sealed partial class FleetWorld
{
    public IReadOnlyList<FleetTargetTag> TargetTags(long actor) =>
        Identity(actor) is { FleetId: var fleet } ? _fleets[fleet].TargetTags : Array.Empty<FleetTargetTag>();

    public void TagTarget(long actor, long fleetId, FleetTagTarget target, string? tag)
    {
        var fleet = MemberFleet(actor, fleetId);
        var member = fleet.Members[actor];
        if (
            member.Placement.Role == FleetRole.Member
            || member.Pilot.Position is not { } position
            || member.Pilot.InTransit
            || target.ItemId <= 0
            || target.ItemId == member.Pilot.ShipId
            || target.SystemId != member.Pilot.SystemId
            || !double.IsFinite(position.DistanceTo(target.Position))
            || position.DistanceTo(target.Position) > FleetRules.CommandRange
        )
        {
            throw new InvalidDataException("A fleet commander must choose a nearby object in space to tag.");
        }
        if (tag is { Length: > 0 } && (tag.Length != 1 || !FleetRules.TargetTagAlphabet.Contains(tag[0])))
        {
            throw new InvalidDataException("Choose a number or letter from the fleet tag menu.");
        }
        string? value = string.IsNullOrEmpty(tag) ? null : tag;
        var previous = fleet.TargetTags.FirstOrDefault(entry => entry.ItemId == target.ItemId);
        if (previous?.Tag == value && (previous is null || previous.SystemId == target.SystemId))
        {
            return;
        }
        // A tag names one target across the fleet. Reassigning it also clears its previous target.
        var tags = fleet.TargetTags.Where(entry => entry.ItemId != target.ItemId && entry.Tag != value).ToList();
        if (value is not null)
        {
            tags.Add(new(target.ItemId, target.SystemId, value));
        }
        fleet.TargetTags = Array.AsReadOnly(tags.OrderBy(entry => entry.ItemId).ToArray());
    }
}
