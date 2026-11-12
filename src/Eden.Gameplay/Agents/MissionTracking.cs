using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Agents;

public sealed record MissionTrackingTarget(int TypeId, long ItemId, SpacePosition Position);

public sealed record MissionTrackingEntry(MissionOffer Offer, IReadOnlyList<SpacePosition> Rooms)
{
    public IReadOnlyList<MissionTrackingTarget> Targets { get; init; } = [];
}

public enum MissionObjectiveKind
{
    Travel,
    TransportMissing,
    TransportPresent,
    DestroyAll,
    Destroy,
    Fetch,
    Mine,
    ReturnMinedCargo,
    Complete,
}

public sealed record MissionTrackerObjective(
    MissionObjectiveKind Kind,
    long AgentId,
    int LocationId = 0,
    int TypeId = 0,
    int Quantity = 0,
    long TargetId = 0
);

public sealed record MissionTrackingState(
    MissionTrackerObjective Current,
    IReadOnlyList<MissionTrackerObjective> Objectives,
    bool InDungeon
);

public static class MissionTrackingRules
{
    public static MissionTrackingState Read(MissionTrackingEntry entry, int systemId, long? stationId, SpacePosition? position)
    {
        var offer = entry.Offer;
        if (offer.State != MissionState.Accepted)
        {
            throw new InvalidDataException("Track accepted missions only.");
        }
        bool inDungeon =
            offer.Kind != MissionKind.Courier
            && systemId == offer.SystemId
            && position is { } point
            && entry.Rooms.Any(room => room.DistanceTo(point) <= MissionRules.ArrivalRange);
        var complete = new MissionTrackerObjective(MissionObjectiveKind.Complete, offer.Agent.Id);
        if (offer.Cargo is { } cargo)
        {
            var missing = new MissionTrackerObjective(
                MissionObjectiveKind.TransportMissing,
                offer.Agent.Id,
                cargo.DestinationStation,
                cargo.TypeId,
                cargo.Quantity
            );
            var present = missing with { Kind = MissionObjectiveKind.TransportPresent };
            var current =
                !offer.ObjectiveComplete ? missing
                : position is null && stationId == cargo.DestinationStation ? complete
                : present;
            return new(current, [missing, present, complete], false);
        }
        var travel = new MissionTrackerObjective(MissionObjectiveKind.Travel, offer.Agent.Id, offer.SystemId);
        if (offer.Kind == MissionKind.Mining && offer.Retrieval is { } ore)
        {
            var mine = new MissionTrackerObjective(MissionObjectiveKind.Mine, offer.Agent.Id, TypeId: ore.TypeId, Quantity: ore.Quantity);
            var returnToAgent = mine with { Kind = MissionObjectiveKind.ReturnMinedCargo, LocationId = offer.Agent.StationId!.Value };
            return new(
                offer.ObjectiveComplete ? (stationId == offer.Agent.StationId ? complete : returnToAgent)
                    : inDungeon ? mine
                    : travel,
                [travel, mine, returnToAgent, complete],
                inDungeon
            );
        }
        var destroy = new MissionTrackerObjective(MissionObjectiveKind.DestroyAll, offer.Agent.Id);
        var target = position is { } at
            ? entry.Targets.FirstOrDefault(target => target.Position.DistanceTo(at) <= MissionRules.ArrivalRange)
            : null;
        var combat = target is null
            ? destroy
            : new MissionTrackerObjective(MissionObjectiveKind.Destroy, offer.Agent.Id, TypeId: target.TypeId, TargetId: target.ItemId);
        var fetch = offer.Retrieval is { } retrieval
            ? new MissionTrackerObjective(
                MissionObjectiveKind.Fetch,
                offer.Agent.Id,
                TypeId: retrieval.TypeId,
                Quantity: retrieval.Quantity
            )
            : null;
        MissionTrackerObjective active =
            offer.ObjectiveComplete ? complete
            : offer.CombatComplete && fetch is not null ? fetch
            : !inDungeon ? travel
            : combat;
        var goals = entry
            .Targets.Select(target => new MissionTrackerObjective(
                MissionObjectiveKind.Destroy,
                offer.Agent.Id,
                TypeId: target.TypeId,
                TargetId: target.ItemId
            ))
            .ToArray();
        List<MissionTrackerObjective> objectives = [travel, destroy];
        objectives.AddRange(goals);
        if (fetch is not null)
        {
            objectives.Add(fetch);
        }
        objectives.Add(complete);
        return new(active, objectives.AsReadOnly(), inDungeon);
    }
}
