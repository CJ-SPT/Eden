using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Repair;

[Injectable(InjectionType.Scoped)]
internal sealed class HardenerHandler(TimeProvider clock, IPilotHardening hardening) : IModuleEffectSource, IDisposable
{
    private sealed record Cycle(
        long PilotId,
        long ShipId,
        Hardener Module,
        long Started,
        long StartTime,
        long Repeats,
        bool Stopping = false
    );

    private static readonly DogmaAttributeId[] ResistanceAttributes =
    [
        DogmaAttributeId.ShieldEmResonance,
        DogmaAttributeId.ShieldExplosiveResonance,
        DogmaAttributeId.ShieldKineticResonance,
        DogmaAttributeId.ShieldThermalResonance,
        DogmaAttributeId.ArmorEmResonance,
        DogmaAttributeId.ArmorExplosiveResonance,
        DogmaAttributeId.ArmorKineticResonance,
        DogmaAttributeId.ArmorThermalResonance,
    ];

    private readonly Dictionary<long, Cycle> _cycles = [];

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.ShipId == pilot.ShipId ? Effect(cycle) : null;

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        foreach (var cycle in _cycles.Values.ToArray())
        {
            if (cycle.ShipId != context.Pilot.ShipId || !context.Flight.CanUseSelfModules)
            {
                Stop(context, cycle);
                continue;
            }
            if (clock.GetElapsedTime(cycle.Started).TotalMilliseconds < cycle.Module.DurationMilliseconds)
            {
                continue;
            }
            if (!cycle.Stopping && cycle.Repeats > 0)
            {
                try
                {
                    Start(context, cycle.Module.ItemId, cycle.Module.Effect, cycle.Repeats - 1, cycle);
                    continue;
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException)
                {
                    // An unpaid repeat removes the old bonus rather than
                    // leaving a permanent resistance marker behind.
                }
            }
            Stop(context, cycle);
        }
    }

    private void Start(PilotCallContext context, long module, DogmaEffect effect, long repeats, Cycle? previous = null)
    {
        long started = clock.GetTimestamp();
        long now = Now();
        var result = hardening.Activate(context.Pilot.PilotId, module, effect, now);
        Cycle cycle = new(context.Pilot.PilotId, context.Pilot.ShipId, result.Module, started, now, repeats);
        _cycles[module] = cycle;
        if (previous is null)
        {
            NotifyEffect(context, cycle, true);
        }
        else
        {
            context.Flight.SetHardenerEffect(Contact(previous), false);
        }
        context.Flight.SetHardenerEffect(Contact(cycle), true);
        NotifyAttributes(context, cycle, result.ShipAttributes, result.Capacitor);
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        var attributes = hardening.Stop(cycle.PilotId, cycle.ShipId, cycle.Module.ItemId);
        _cycles.Remove(cycle.Module.ItemId);
        context.Flight.SetHardenerEffect(Contact(cycle), false);
        NotifyEffect(context, cycle, false);
        NotifyAttributes(context, cycle, attributes);
    }

    private void NotifyAttributes(
        PilotCallContext context,
        Cycle cycle,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        double? capacitor = null
    )
    {
        List<ModuleAttributeChange> changes = ResistanceAttributes
            .Where(attributes.ContainsKey)
            .Select(a => Change(a, attributes[a]))
            .ToList();
        if (capacitor is { } charge)
        {
            changes.Add(Change(DogmaAttributeId.CapacitorCharge, charge));
        }
        if (changes.Count > 0)
        {
            context.Notifications.Add(new ModuleAttributesChanged(changes));
        }
        ModuleAttributeChange Change(DogmaAttributeId attribute, double value) =>
            new(cycle.PilotId, new DogmaItemKey.Item(cycle.ShipId), attribute, Now(), value);
    }

    private static HardenerContact Contact(Cycle cycle) => new(cycle.Module, cycle.StartTime);

    private static DogmaActiveEffect Effect(Cycle cycle) =>
        new(
            cycle.Module.ItemId,
            cycle.PilotId,
            cycle.ShipId,
            null,
            cycle.Module.Effect,
            cycle.StartTime,
            cycle.Module.DurationMilliseconds,
            cycle.Repeats
        );

    private void NotifyEffect(PilotCallContext context, Cycle cycle, bool active) =>
        context.Notifications.Add(new ShipEffectChanged(Effect(cycle), Now(), active, active, active ? null : Now()));

    private long Now() => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public PilotCallResult Call(PilotCallContext context, string service, string method, object?[] args)
    {
        if (service != "dogmaIM" || method is not ("Activate" or "Deactivate") || args.Length < 2)
        {
            return PilotCallResult.Unsupported;
        }
        string? name = BlueMarshal.Text(args[1]);
        DogmaEffect? requested = name switch
        {
            HardenerWireDefinitions.ShieldEffectName => DogmaEffect.ActiveShieldResistance,
            HardenerWireDefinitions.ArmorEffectName => DogmaEffect.ActiveArmorResistance,
            _ => null,
        };
        if (requested is null)
        {
            return PilotCallResult.Unsupported;
        }
        context.Flight.Checkpoint();
        Advance(context);
        try
        {
            if (method == "Activate" && args is [long module, _, var target, long repeats] && repeats is >= 0 and <= int.MaxValue)
            {
                if (!context.Flight.CanUseSelfModules || target is not null && !Equals(target, context.Pilot.ShipId))
                {
                    return new PilotCallResult.Failure("Activate the hardener on your own ship in space.");
                }
                if (!_cycles.ContainsKey(module))
                {
                    Start(context, module, requested.Value, repeats);
                }
                return PilotCallResult.None;
            }
            if (method == "Deactivate" && args is [long moduleId, _])
            {
                if (_cycles.TryGetValue(moduleId, out var cycle))
                {
                    _cycles[moduleId] = cycle with { Stopping = true };
                }
                return PilotCallResult.None;
            }
            return PilotCallResult.Unsupported;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The hardener exceeded the local cycle limit.");
        }
    }

    public void Dispose()
    {
        foreach (var cycle in _cycles.Values)
        {
            _ = hardening.Stop(cycle.PilotId, cycle.ShipId, cycle.Module.ItemId);
        }
        _cycles.Clear();
    }
}
