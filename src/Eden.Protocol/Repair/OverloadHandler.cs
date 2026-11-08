using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Repair;

[Injectable(InjectionType.Scoped)]
internal sealed class OverloadHandler(
    IPilotOverloading overload,
    TimeProvider clock,
    ModuleBurnoutMonitor monitor,
    IEnumerable<IModuleEffectSource> effects
) : ISelectedPilotCallHandler, IDisposable
{
    private readonly Dictionary<long, ModuleOverloadState> _pending = [];
    internal bool IsMonitoring => _pending.Count > 0;
    private long _pilot;
    private long _ship;
    private int _system;

    public void Dispose() => Clear();

    internal void Clear()
    {
        if (_ship != 0)
        {
            overload.Clear(_pilot, _ship);
        }
        _ship = 0;
        _pending.Clear();
    }

    internal void Advance(PilotCallContext context)
    {
        if (
            _ship != 0
            && (
                !context.Selected
                || context.Pilot.ShipId != _ship
                || context.Pilot.SystemId != _system
                || context.Pilot.Location.Position is null
            )
        )
        {
            Clear();
        }
        if (_pending.Count == 0)
        {
            return;
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var requested = overload.Requested(_pilot, _ship);
        foreach (var state in _pending.Values.ToArray())
        {
            if (!requested.Contains(state.ModuleId) || monitor.IsOverloaded(state.ModuleId))
            {
                _pending.Remove(state.ModuleId);
            }
            else if (effects.All(source => source.ActiveEffect(context.Pilot, state.ModuleId) is null))
            {
                Notify(context, [state with { Pending = false }], now);
                _pending.Remove(state.ModuleId);
            }
        }
    }

    private void Notify(PilotCallContext context, IReadOnlyList<ModuleOverloadState> states, long now)
    {
        monitor.Selection(context, states, now);
        foreach (var state in states.Where(s => !s.Pending))
        {
            context.Notifications.Add(
                new ShipEffectChanged(
                    new(state.ModuleId, _pilot, _ship, null, (DogmaEffect)state.EffectId, now, null, 0),
                    now,
                    state.Requested,
                    state.Requested,
                    state.Requested ? null : now
                )
            );
        }
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "dogmaIM" || method is not ("Overload" or "StopOverload" or "OverloadRack" or "StopOverloadRack"))
        {
            return PilotCallResult.Unsupported;
        }
        bool rack = method.EndsWith("Rack", StringComparison.Ordinal);
        bool enabled = method is "Overload" or "OverloadRack";
        if (
            args.Length != (rack ? 1 : 2)
            || args[0] is not long module
            || module <= 0
            || !rack && (args[1] is not long effect || effect <= 0 || effect > int.MaxValue)
        )
        {
            return new PilotCallResult.Failure("Invalid overload selection.");
        }
        try
        {
            Advance(context);
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            var states = overload.Set(
                new(
                    context.Pilot.PilotId,
                    context.Pilot.ShipId,
                    module,
                    rack ? null : checked((int)(long)args[1]!),
                    rack ? OverloadScope.Rack : OverloadScope.Module,
                    enabled,
                    now
                )
            );
            _pilot = context.Pilot.PilotId;
            _ship = context.Pilot.ShipId;
            _system = context.Pilot.SystemId;
            foreach (var state in states)
            {
                if (state.Pending && state.Requested)
                {
                    _pending[state.ModuleId] = state;
                }
                else
                {
                    _pending.Remove(state.ModuleId);
                }
            }
            Notify(context, states, now);
            return rack
                ? new PilotCallResult.Success(new OverloadRackReply(states.Select(s => s.ModuleId).ToArray()))
                : PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record OverloadRackReply(IReadOnlyList<long> Modules) : BlueModel
{
    public override object ToBlue() => Modules.Select(m => (object?)m).ToList();
}
