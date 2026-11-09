using Eden.Gameplay.Exploration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

internal enum HackingEventId
{
    Lost = 0,
    Won = 1,
    Start = 2,
    Virus = 3,
    Killed = 4,
    Style = 5,
    Acknowledge = 6,
    Flipped = 100,
    Created = 101,
    Blocked = 102,
    PickedUp = 200,
    Removed = 201,
    InUse = 202,
    Attack = 301,
    Repair = 303,
    Vector = 304,
    ShieldHit = 305,
    Strength = 306,
    Distance = 308,
}

internal sealed record HackingEvents(
    HackingContact Contact,
    HackingBoard Current,
    HackingBoard? Previous = null,
    HackingAction? Action = null
) : BlueModel
{
    private static object[] Coordinate(HackingCoordinate c) => [c.X, c.Y];

    private static Dictionary<string, object?> Tile(HackingTile t) =>
        new()
        {
            ["id"] = t.Id,
            ["coord"] = Coordinate(t.Coordinate),
            ["type"] = t.Hidden ? (int)HackingNode.Empty : (int)t.Type,
            ["subtype"] = t.Hidden ? (int)HackingSubsystem.None : (int)t.Subtype,
            ["coherence"] = t.Hidden ? 0 : t.Coherence,
            ["strength"] = t.Hidden ? 0 : t.Strength,
            ["hidden"] = t.Hidden,
            ["blocked"] = t.Blocked,
        };

    private static Dictionary<string, object?> Virus(HackingBoard board) =>
        new()
        {
            ["id"] = 1,
            ["type"] = (int)HackingNode.Virus,
            ["coherence"] = board.Coherence,
            ["strength"] = board.Strength,
        };

    private static List<object?> Inventory(HackingBoard board) =>
        board
            .Inventory.Select(
                (u, i) =>
                    (object?)
                        new Dictionary<string, object?>
                        {
                            ["id"] = u.Id,
                            ["index"] = i,
                            ["subtype"] = (int)u.Subtype,
                            ["isInUse"] = u.Active,
                            ["durationRemaining"] = u.Remaining,
                            ["totalDuration"] = HackingRules.UtilityDuration,
                            ["info"] = null,
                        }
            )
            .ToList();

    private static Dictionary<string, object?> Event(HackingEventId id, Dictionary<string, object?> data) =>
        new() { ["eventID"] = (int)id, ["eventData"] = data };

    public override object ToBlue()
    {
        List<object?> events = [];
        if (Previous is null)
        {
            events.Add(Event(HackingEventId.Style, new() { ["style"] = (int)Contact.Kind }));
            foreach (var tile in Current.Tiles)
            {
                events.Add(Event(HackingEventId.Created, Tile(tile)));
            }
            var virus = Virus(Current);
            virus["inventory"] = Inventory(Current);
            events.Add(Event(HackingEventId.Virus, virus));
            events.Add(Event(HackingEventId.Start, new() { ["moduleTypeID"] = Contact.Module.TypeId, ["targetID"] = Contact.ContainerId }));
        }
        else
        {
            var before = Previous.Tiles.ToDictionary(t => t.Id);
            foreach (var tile in Current.Tiles.Where(t => !t.Hidden && before[t.Id] != t))
            {
                if (
                    Action is { Kind: HackingActionKind.Click } click
                    && click.Coordinate == tile.Coordinate
                    && !before[tile.Id].Hidden
                    && before[tile.Id].Type is HackingNode.Core or HackingNode.Defense
                )
                {
                    var defender = Tile(tile with { Type = before[tile.Id].Type, Subtype = before[tile.Id].Subtype });
                    events.Add(Event(HackingEventId.Attack, new() { ["attackerResult"] = Virus(Current), ["defenderResult"] = defender }));
                }
                events.Add(Event(HackingEventId.Flipped, Tile(tile)));
                if (tile.Type == HackingNode.Segment && before[tile.Id].Hidden)
                {
                    var hint = Tile(tile);
                    hint["distanceIndicator"] = HackingRules.DistanceHint(Current, tile.Coordinate);
                    events.Add(Event(HackingEventId.Distance, hint));
                }
            }
            foreach (var tile in Current.Tiles.Where(t => t.Hidden && before[t.Id].Blocked != t.Blocked))
            {
                events.Add(Event(HackingEventId.Blocked, Tile(tile)));
            }
            for (int index = 0; index < Current.Inventory.Count; index++)
            {
                var old = Previous.Inventory[index];
                var current = Current.Inventory[index];
                if (current == old)
                {
                    continue;
                }
                if (old.Subtype == HackingSubsystem.None && current.Subtype != HackingSubsystem.None)
                {
                    var replacement = Current.Tiles.First(t => t.Id == current.Id);
                    events.Add(
                        Event(
                            HackingEventId.PickedUp,
                            new()
                            {
                                ["coord"] = Coordinate(replacement.Coordinate),
                                ["replacementObject"] = Tile(replacement),
                                ["inventory"] = Inventory(Current),
                            }
                        )
                    );
                }
                else if (current.Subtype == HackingSubsystem.None)
                {
                    if (old.Subtype == HackingSubsystem.SelfRepair && old.Active)
                    {
                        events.Add(
                            Event(
                                HackingEventId.Repair,
                                new()
                                {
                                    ["index"] = index,
                                    ["durationRemaining"] = 0,
                                    ["target"] = Virus(Current),
                                }
                            )
                        );
                    }
                    events.Add(Event(HackingEventId.Removed, new() { ["index"] = index }));
                }
                else
                {
                    events.Add(Event(HackingEventId.InUse, new() { ["index"] = index, ["value"] = current.Active }));
                    if (current.Subtype == HackingSubsystem.SelfRepair)
                    {
                        events.Add(
                            Event(
                                HackingEventId.Repair,
                                new()
                                {
                                    ["index"] = index,
                                    ["durationRemaining"] = current.Remaining,
                                    ["target"] = Virus(Current),
                                }
                            )
                        );
                    }
                    if (current.Subtype == HackingSubsystem.Shield && old.Active && current.Remaining < old.Remaining)
                    {
                        events.Add(Event(HackingEventId.ShieldHit, new() { ["index"] = index, ["durationRemaining"] = current.Remaining }));
                    }
                    if (current.Subtype == HackingSubsystem.SecondaryVector && current.Target is { } coordinate)
                    {
                        var target = Tile(Current.Tiles.Single(t => t.Coordinate == coordinate));
                        target["index"] = index;
                        target["durationRemaining"] = current.Remaining;
                        events.Add(Event(HackingEventId.Vector, target));
                    }
                }
            }
            if (Current.Strength != Previous.Strength)
            {
                events.Add(Event(HackingEventId.Strength, new() { ["virus"] = Virus(Current) }));
            }
            events.Add(Event(HackingEventId.Acknowledge, []));
        }
        if (Current.Outcome != HackingOutcome.Running)
        {
            events.Add(
                Event(
                    Current.Outcome == HackingOutcome.Won ? HackingEventId.Won : HackingEventId.Lost,
                    new() { ["targetID"] = Contact.ContainerId }
                )
            );
        }
        return events;
    }
}

internal sealed record HackingTurnCompleted(
    HackingContact Contact,
    HackingBoard Board,
    HackingBoard? Previous = null,
    HackingAction? Action = null
) : PilotNotification("OnHackingTurnComplete")
{
    public override object?[] Arguments => [new HackingEvents(Contact, Board, Previous, Action)];
}
