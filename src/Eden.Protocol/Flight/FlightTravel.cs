using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

internal sealed record GateDestination(
    long SourceId,
    StaticSpaceObject Exit,
    FlightSystem System,
    IReadOnlyList<StaticSpaceObject> Objects,
    IWormholeTravel? Wormhole = null
);

internal sealed record GateTransit(GateDestination Destination, int TransferStamp);

internal sealed record TranslocationTransit(
    IPilotTranslocation Travel,
    TranslocationTarget Target,
    IReadOnlyList<StaticSpaceObject> Objects,
    int TransferStamp,
    bool AwaitingSourceState = false,
    int? PublishedSourceStamp = null
);

internal readonly record struct FlightTravelContext(DockedPilot Pilot, bool InBallpark, bool InWarp, bool Aligning);

internal sealed record FlightJumpDecision(PilotCallResult Result, GateDestination? Destination = null);

internal sealed class FlightTravel
{
    internal const int ArrivalDurationTicks = GateTravelRules.ArrivalTicks;

    // The pinned effect defaults to 10,000 ms. The departure packet overrides its duration
    // with Eden's shorter authoritative gate transition.
    internal const int JumpDurationTicks = GateTravelRules.DurationTicks;

    // Allow the pinned cyno camera's look phase and source-scene handoff to begin.
    internal const int TranslocationDurationTicks = 3;

    internal GateTransit? Gate { get; private set; }
    internal TranslocationTransit? Translocation { get; private set; }
    internal bool Translocating => Translocation is not null;
    internal bool TranslocationArrival { get; private set; }
    internal GateArrivalContact? Arrival { get; private set; }
    internal long? WormholeArrival { get; private set; }
    internal long? DepartureAt { get; private set; }
    private bool _awaitingArrival;
    private int? _arrivalStamp;

    internal bool IsArriving(int stamp) => stamp - _arrivalStamp < ArrivalDurationTicks;

    internal void Reset()
    {
        Gate = null;
        Translocation = null;
        TranslocationArrival = false;
        Arrival = null;
        WormholeArrival = null;
        DepartureAt = null;
        _awaitingArrival = false;
        _arrivalStamp = null;
    }

    internal void CancelTranslocation() => Translocation = null;

    internal void ArrivedAt(int stamp) => _arrivalStamp = stamp;

    internal void ExpectArrival() => _awaitingArrival = true;

    internal void BeginGate(GateDestination destination, int stamp, long departedAt)
    {
        _arrivalStamp = null;
        Arrival = null;
        WormholeArrival = null;
        DepartureAt = departedAt;
        Gate = new(destination, stamp + JumpDurationTicks);
    }

    internal GateDestination? GateDue(int stamp) => Gate is { } gate && stamp >= gate.TransferStamp ? gate.Destination : null;

    internal FlightJumpDecision PrepareGate(
        FlightTravelContext context,
        FlightCommand.Jump request,
        IReadOnlyList<StaticSpaceObject> objects,
        ISystemObjects systems,
        IPilotCrimewatch? crimewatch
    )
    {
        if (
            !context.InBallpark
            || Translocating
            || context.Pilot.Position is not { } position
            || Gate is not null
            || context.InWarp
            || context.Aligning
            || request.ShipId != context.Pilot.ShipId
            || objects.FirstOrDefault(o => o.Id == request.SourceId) is not { DestinationSystemId: int systemId } source
            || source.DestinationGateId != request.ExitId
            || !GateTravelRules.InRange(position, context.Pilot.Flight.ShipRadius, source)
        )
        {
            return new(PilotCallResult.Unsupported);
        }
        if (CheckPermission(crimewatch, context.Pilot.PilotId, CrimewatchTravel.Jump, systemId) is { } blocked)
        {
            return new(blocked);
        }
        var destination = systems.Load(systemId);
        var exit = destination.FirstOrDefault(o =>
            o.Id == request.ExitId && o.DestinationGateId == source.Id && o.DestinationSystemId == context.Pilot.SystemId
        );
        return exit is null
            ? new(PilotCallResult.Unsupported)
            : new(PilotCallResult.None, new(source.Id, exit, systems.GetSystem(systemId), destination));
    }

    internal void CompleteGate(long arrivalGate, long arrivedAt, bool wormhole = false)
    {
        Gate = null;
        DepartureAt = null;
        Arrival = wormhole ? null : new(arrivalGate, arrivedAt);
        WormholeArrival = wormhole ? arrivalGate : null;
        _awaitingArrival = true;
    }

    internal void BeginTranslocation(
        IPilotTranslocation travel,
        TranslocationTarget target,
        IReadOnlyList<StaticSpaceObject> objects,
        int stamp
    )
    {
        Gate = null;
        _arrivalStamp = null;
        Arrival = null;
        _awaitingArrival = false;
        Translocation = new(travel, target, objects, stamp + TranslocationDurationTicks);
    }

    internal void QueueTranslocation(IPilotTranslocation travel, TranslocationTarget target, IReadOnlyList<StaticSpaceObject> objects) =>
        Translocation = new(travel, target, objects, int.MaxValue, AwaitingSourceState: true);

    internal void SourceStatePublished(int stamp)
    {
        if (Translocation is { AwaitingSourceState: true, PublishedSourceStamp: null } pending)
        {
            Translocation = pending with { PublishedSourceStamp = stamp };
        }
    }

    internal TranslocationTransit? DepartureReady(int stamp) =>
        Translocation is { AwaitingSourceState: true, PublishedSourceStamp: int published } pending && stamp > published ? pending : null;

    internal TranslocationTransit? TranslocationDue(int stamp) =>
        Translocation is { AwaitingSourceState: false } transit && stamp >= transit.TransferStamp ? transit : null;

    internal void CompleteTranslocation()
    {
        _awaitingArrival = true;
        TranslocationArrival = true;
    }

    internal bool BeginArrival(bool entering, int stamp)
    {
        if (!entering || !_awaitingArrival)
        {
            return false;
        }
        _awaitingArrival = false;
        _arrivalStamp = stamp;
        return true;
    }

    internal void ArrivalEffectSent() => TranslocationArrival = false;

    internal static PilotCallResult.Failure? CheckPermission(
        IPilotCrimewatch? crimewatch,
        long pilotId,
        CrimewatchTravel action,
        int? destination = null
    )
    {
        try
        {
            crimewatch?.CheckTravel(pilotId, action, destination);
            return null;
        }
        catch (InvalidDataException error)
        {
            return new(error.Message, ShowInformationDialog: action == CrimewatchTravel.Warp);
        }
    }
}
