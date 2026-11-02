using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal interface IPilotCallHandler
{
    object? Call(PilotCallContext context, string service, string method, object? arguments, Dictionary<string, object?>? keywords);
}

internal interface ISelectedPilotCallHandler
{
    object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null);
}

internal sealed class PilotCallContext(long accountId, AccountProfile account, IReadOnlyList<DockedPilot> roster,
    Func<DockedPilot, FlightSession> startFlight, long sessionId, object?[] signedSettings) : IDisposable
{
    public long AccountId { get; } = accountId;
    public AccountProfile Account { get; } = account;
    public IReadOnlyList<DockedPilot> Roster { get; set; } = roster;
    public FlightSession? ActiveFlight { get; private set; }
    public FlightSession Flight => ActiveFlight ?? throw new InvalidOperationException("Select a pilot before entering flight.");
    public void Select(DockedPilot pilot) => ActiveFlight = startFlight(pilot);
    public void Dispose() => ActiveFlight?.Dispose();
    public DockedPilot Pilot => Flight.Pilot;
    public object?[] SignedSettings { get; } = signedSettings;
    public bool Selected => ActiveFlight is not null;
    public long? NextSkillCompletion { get; set; }
    public List<(string Name, object?[] Arguments)> Notifications { get; } = [];

    private readonly Dictionary<string, string> _bindings = [];
    private readonly Dictionary<string, BlueObjectReference> _references = [];

    private int _nextSlot = 2;

    public void ReleaseLocationReferences()
    {
        foreach (string service in new[] { "beyonce", "ship", "invbroker", "dogmaIM", "jumpCloneSvc", "corpStationMgr" })
        {
            if (_references.Remove(service, out var reference))
            {
                _bindings.Remove(reference.Id);
            }
        }
    }

    public string? ResolveReference(object? objectId) =>
        objectId is byte[] or string ? _bindings.GetValueOrDefault(BlueMarshal.Text(objectId)) : null;

    public BlueObjectReference Reference(string service)
    {
        if (!_references.TryGetValue(service, out var reference))
        {
            int slot = _nextSlot++;
            reference = new($"N={PilotLogin.NodeId}:{sessionId}-{slot}", slot);
            _references.Add(service, reference);
            _bindings.Add(reference.Id, service);
        }
        return reference;
    }
}

[Injectable(InjectionType.Scoped)]
internal sealed class PilotCallRouter(IEnumerable<IPilotCallHandler> accountHandlers, IEnumerable<ISelectedPilotCallHandler> selectedHandlers)
{
    // A successful MachoNet method can return Python None.
    public static readonly object Unsupported = new();
    internal sealed record Error(string Message);
    internal sealed record UserError(string Name, Dictionary<string, object?> Values);

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

    public object? Call(PilotCallContext context, string service, string method, object? arguments, Action<string> report, Dictionary<string, object?>? keywords = null)
    {
        foreach (var handler in accountHandlers)
        {
            object? result = handler.Call(context, service, method, arguments, keywords);
            if (!ReferenceEquals(result, Unsupported))
            {
                return result;
            }
        }
        return context.Selected && arguments is object?[] args ? CallSelected(context, service, method, args, report, keywords) : Unsupported;
    }

    private object? CallSelected(PilotCallContext context, string service, string method, object?[] args, Action<string> report, Dictionary<string, object?>? keywords)
    {
        if (service is "jumpCloneSvc" or "invbroker" or "dogmaIM" or "corpRegistry" or "crimewatch" or "warRegistry" or "ship" or "insuranceSvc" or "beyonce" or "corpStationMgr")
        {
            if (method == "MachoResolveObject")
            {
                return PilotLogin.NodeId;
            }
            if (method == "MachoBindObject" && args.Length == 2)
            {
                long locationGroup = context.Pilot.Position is null ? 15 : 5;
                if (service == "corpStationMgr" && (context.Pilot.Position is not null || !Equals(args[0], (long)context.Pilot.StationId)))
                {
                    return Unsupported;
                }
                if (service is "invbroker" or "ship" or "jumpCloneSvc" && args[0] is not null &&
                    (args[0] is not object?[] bind || bind is not [long location, long group] || location != context.Pilot.LocationId || group != locationGroup))
                {
                    return Unsupported;
                }
                if (service == "beyonce" && (context.Pilot.Position is null || !Equals(args[0], (long)context.Pilot.SystemId)))
                {
                    return Unsupported;
                }
                object? result = null;
                if (args[1] is object?[] { Length: >= 2 } first && first[1] is object?[] innerArgs)
                {
                    string innerMethod = BlueMarshal.Text(first[0]);
                    report($"Bound call {service}.{innerMethod}.");
                    result = CallSelected(context, service, innerMethod, innerArgs, report, first.ElementAtOrDefault(2) as Dictionary<string, object?>);
                }
                else if (args[1] is not null)
                {
                    return Unsupported;
                }
                return ReferenceEquals(result, Unsupported) ? Unsupported : new object?[] { context.Reference(service), result };
            }
        }
        foreach (var handler in selectedHandlers)
        {
            object? result = handler.Call(context, service, method, args, keywords);
            if (!ReferenceEquals(result, Unsupported))
            {
                return result;
            }
        }
        return Unsupported;
    }
}
