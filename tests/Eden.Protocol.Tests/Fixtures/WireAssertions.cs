global using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.Fixtures;

// Existing wire assertions intentionally inspect outbound constructors before encoding.
// Expand typed adapters without decoding DBRows or changing integer CLR widths.
internal static class WireAssertions
{
    internal static object? Materialize(object? value) =>
        value switch
        {
            IBlueWritable model => Materialize(model.ToBlue()),
            BlueInstance instance => instance with { State = Materialize(instance.State) },
            BlueConstructor constructor => constructor with
            {
                Arguments = constructor.Arguments.Select(Materialize).ToArray(),
                State = Materialize(constructor.State),
            },
            BlueNewObject instance => instance with { State = Materialize(instance.State) },
            BlueDictionary dictionary => new BlueDictionary(
                dictionary.Entries.Select(p => (Materialize(p.Key), Materialize(p.Value))).ToArray()
            ),
            object?[] tuple => tuple.Select(Materialize).ToArray(),
            List<object?> list => list.Select(Materialize).ToList(),
            Dictionary<long, object?> map => map.ToDictionary(p => p.Key, p => Materialize(p.Value)),
            Dictionary<string, object?> map => map.ToDictionary(p => p.Key, p => Materialize(p.Value)),
            _ => value,
        };

    internal static object? CallWire(
        this ISelectedPilotCallHandler handler,
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    ) => Materialize(handler.Call(context, service, method, args, keywords));

    internal static object? CallWire(
        this IPilotCallHandler handler,
        PilotCallContext context,
        string service,
        string method,
        object? args,
        Dictionary<string, object?>? keywords = null
    ) => Materialize(handler.Call(context, service, method, args, keywords));

    internal static object? CallWire(
        this PilotCallRouter router,
        PilotCallContext context,
        string service,
        string method,
        object? args,
        Action<string> report,
        Dictionary<string, object?>? keywords = null
    ) => Materialize(router.Call(context, service, method, args, report, keywords));

    internal static object? CallWire(
        this FlightSession flight,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    ) => Materialize(flight.Call(service, method, args, keywords));

    internal static object? CallWire(
        this Eden.Protocol.Combat.CombatHandler handler,
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    ) => Materialize(handler.Call(context, service, method, args, keywords));

    internal static object? CallWire(
        this Eden.Protocol.Mining.MiningHandler handler,
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    ) => Materialize(handler.Call(context, service, method, args, keywords));

    internal static object? CallWire(
        this Eden.Protocol.Repair.ShieldBoostHandler handler,
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    ) => Materialize(handler.Call(context, service, method, args));

    internal static object? CallWire(
        this Eden.Protocol.Propulsion.PropulsionHandler handler,
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    ) => Materialize(handler.Call(context, service, method, args));
}
