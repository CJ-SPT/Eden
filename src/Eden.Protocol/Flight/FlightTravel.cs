using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

internal sealed record GateDestination(
    long SourceId,
    StaticSpaceObject Exit,
    FlightSystem System,
    IReadOnlyList<StaticSpaceObject> Objects
);

internal sealed record GateTransit(GateDestination Destination, int TransferStamp);

internal sealed record TranslocationTransit(
    IPilotTranslocation Travel,
    TranslocationTarget Target,
    IReadOnlyList<StaticSpaceObject> Objects,
    int TransferStamp,
    bool AwaitingSourceState = false
);

internal readonly record struct FlightTravelContext(DockedPilot Pilot, bool InBallpark, bool InWarp, bool Aligning);

internal sealed record FlightJumpDecision(PilotCallResult Result, GateDestination? Destination = null);

internal sealed class FlightTravel
{
    private const double GateActivationRange = 2500;
    internal const int ArrivalDurationTicks = 8;

    // Build 3662168 client/effects defines effects.JumpOut with duration 10,000 ms.
    internal const int JumpDurationTicks = 10;

    // Allow the pinned cyno camera's look phase and source-scene handoff to begin.
    internal const int TranslocationDurationTicks = 3;

    internal GateTransit? Gate { get; private set; }
    internal TranslocationTransit? Translocation { get; private set; }
    internal bool Translocating => Translocation is not null;
    internal bool TranslocationArrival { get; private set; }
    private bool _awaitingArrival;
    private int? _arrivalStamp;

    internal bool IsArriving(int stamp) => stamp - _arrivalStamp < ArrivalDurationTicks;

    internal void Reset()
    {
        Gate = null;
        Translocation = null;
        TranslocationArrival = false;
        _awaitingArrival = false;
        _arrivalStamp = null;
    }

    internal void CancelTranslocation() => Translocation = null;

    internal void ArrivedAt(int stamp) => _arrivalStamp = stamp;

    internal void BeginGate(GateDestination destination, int stamp)
    {
        _arrivalStamp = null;
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
            || position.DistanceTo(source.Position) > source.Radius + context.Pilot.Flight.ShipRadius + GateActivationRange
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

    internal void CompleteGate()
    {
        Gate = null;
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
        _awaitingArrival = false;
        Translocation = new(travel, target, objects, stamp + TranslocationDurationTicks);
    }

    internal void QueueTranslocation(IPilotTranslocation travel, TranslocationTarget target, IReadOnlyList<StaticSpaceObject> objects) =>
        Translocation = new(travel, target, objects, int.MaxValue, AwaitingSourceState: true);

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
            return new(error.Message);
        }
    }
}
