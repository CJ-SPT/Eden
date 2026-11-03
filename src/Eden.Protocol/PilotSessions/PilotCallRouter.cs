using Eden.Gameplay.Docking;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Loot;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal interface IPilotCallHandler
{
    PilotCallResult Call(PilotCallContext context, string service, string method, object? arguments, Dictionary<string, object?>? keywords);
}

internal interface ISelectedPilotCallHandler
{
    PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    );
}

internal sealed class PilotCallContext(
    long accountId,
    AccountProfile account,
    IReadOnlyList<DockedPilot> roster,
    Func<DockedPilot, FlightSession> startFlight,
    long sessionId,
    SignedSettings signedSettings
) : IDisposable
{
    public long AccountId { get; } = accountId;
    public AccountProfile Account { get; } = account;
    public IReadOnlyList<DockedPilot> Roster { get; set; } = roster;
    public FlightSession? ActiveFlight { get; private set; }
    public DockedPilot? DestroyedShip { get; private set; }
    public FlightSession Flight => ActiveFlight ?? throw new InvalidOperationException("Select a pilot before entering flight.");

    public void Select(DockedPilot pilot)
    {
        DestroyedShip = null;
        ActiveFlight = startFlight(pilot);
    }

    public void ChangeShip(DockedPilot pilot)
    {
        DestroyedShip = null;
        ActiveFlight?.Dispose();
        ReleaseLocationReferences();
        ActiveFlight = startFlight(pilot);
        Roster = Roster.Select(p => p.PilotId == pilot.PilotId ? pilot : p).ToArray();
    }

    public void ReplaceDestroyedShip(DockedPilot pilot)
    {
        DestroyedShip = Pilot;
        Flight.ReplaceDestroyedShip(pilot);
        Roster = Roster.Select(p => p.PilotId == pilot.PilotId ? pilot : p).ToArray();
    }

    public void Dispose() => ActiveFlight?.Dispose();

    public DockedPilot Pilot => Flight.Pilot;
    public SignedSettings SignedSettings { get; } = signedSettings;
    public bool Selected => ActiveFlight is not null;
    public long? NextSkillCompletion { get; set; }
    public List<PilotNotification> Notifications { get; } = [];

    // Combat text resolves the target's slim item, including on a fatal hit.
    // Deliver it before Destiny removes that ball from the client's park.
    public List<PilotNotification> NotificationsBeforeFlight { get; } = [];

    private readonly PilotBindings _bindings = new(sessionId);

    public void ReleaseLocationReferences()
    {
        DestroyedShip = null;
        _bindings.ReleaseLocationReferences();
    }

    public string? ResolveReference(object? objectId) => _bindings.ResolveReference(objectId);

    public BlueObjectReference Reference(string service, PilotBindingLifetime lifetime = PilotBindingLifetime.Pilot) =>
        _bindings.Reference(service, lifetime);
}

[Injectable(InjectionType.Scoped)]
internal sealed class PilotCallRouter(
    IEnumerable<IPilotCallHandler> accountHandlers,
    IEnumerable<ISelectedPilotCallHandler> selectedHandlers
)
{
    public string ResolveService(PilotCallContext context, object?[] route, object? objectId)
    {
        return route switch
        {
            [1L, (long)PilotLogin.NodeId, null, _] when context.Selected => context.ResolveReference(objectId) ?? "unknown binding",
            [1L, _, null, _] => "unknown binding",
            [1L, _, var name, _] => NamedService(name),
            [8L, var name, _] => NamedService(name),
            _ => throw new InvalidDataException("Invalid service address."),
        };
    }

    private static string NamedService(object? value)
    {
        string name = BlueMarshal.Text(value);
        return name.StartsWith('@') ? "unknown binding" : name;
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Action<string> report,
        Dictionary<string, object?>? keywords = null
    )
    {
        foreach (var handler in accountHandlers)
        {
            PilotCallResult result = handler.Call(context, service, method, arguments, keywords);
            if (result is not PilotCallResult.UnsupportedCall)
            {
                return result;
            }
        }
        return context.Selected && arguments is object?[] args
            ? CallSelected(context, service, method, args, report, keywords)
            : PilotCallResult.Unsupported;
    }

    private PilotCallResult CallSelected(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Action<string> report,
        Dictionary<string, object?>? keywords
    )
    {
        if (
            service
            is "jumpCloneSvc"
                or "invbroker"
                or "dogmaIM"
                or "corpRegistry"
                or "crimewatch"
                or "warRegistry"
                or "ship"
                or "insuranceSvc"
                or "beyonce"
                or "corpStationMgr"
                or "reprocessingSvc"
                or "repairSvc"
        )
        {
            if (method == "MachoResolveObject")
            {
                return PilotLogin.NodeId;
            }
            if (method == "MachoBindObject" && args.Length == 2)
            {
                long locationGroup = context.Pilot.Position is null ? 15 : 5;
                if (
                    service == "repairSvc"
                    && (
                        context.Pilot.Position is not null
                        || args[0] is not object?[] repairBind
                        || repairBind is not [long repairStation, long repairLocation, (long)RepairDefinitions.StationGroup]
                        || repairStation != context.Pilot.StationId
                        || repairLocation != repairStation
                    )
                )
                {
                    return PilotCallResult.Unsupported;
                }
                if (
                    service is "corpStationMgr" or "reprocessingSvc"
                    && (context.Pilot.Position is not null || !Equals(args[0], (long)context.Pilot.StationId))
                )
                {
                    return PilotCallResult.Unsupported;
                }
                if (
                    service is "invbroker" or "ship" or "jumpCloneSvc"
                    && args[0] is not null
                    && (
                        args[0] is not object?[] bind
                        || bind is not [long location, long group]
                        || location != context.Pilot.LocationId
                        || group != locationGroup
                    )
                )
                {
                    return PilotCallResult.Unsupported;
                }
                if (service == "beyonce" && (context.Pilot.Position is null || !Equals(args[0], (long)context.Pilot.SystemId)))
                {
                    return PilotCallResult.Unsupported;
                }
                PilotCallResult result = PilotCallResult.None;
                if (args[1] is object?[] { Length: >= 2 } first && first[1] is object?[] innerArgs)
                {
                    string innerMethod = BlueMarshal.Text(first[0]);
                    report($"Bound call {service}.{innerMethod}.");
                    result = CallSelected(
                        context,
                        service,
                        innerMethod,
                        innerArgs,
                        report,
                        first.ElementAtOrDefault(2) as Dictionary<string, object?>
                    );
                }
                else if (args[1] is not null)
                {
                    return PilotCallResult.Unsupported;
                }
                // A failed first call is an RPC failure, not a value to embed in
                // the successful (bound reference, result) tuple.
                return result is PilotCallResult.Success success
                    ? new PilotCallResult.Success(
                        new BoundPilotReply(context.Reference(service, PilotBindingLifetime.Location), success.Reply)
                    )
                    : result;
            }
        }
        foreach (var handler in selectedHandlers)
        {
            PilotCallResult result = handler.Call(context, service, method, args, keywords);
            if (result is not PilotCallResult.UnsupportedCall)
            {
                return result;
            }
        }
        return PilotCallResult.Unsupported;
    }
}
