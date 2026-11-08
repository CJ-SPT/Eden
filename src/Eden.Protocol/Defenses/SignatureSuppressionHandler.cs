using Eden.Gameplay.Defenses;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Defenses;

[Injectable(InjectionType.Scoped)]
internal sealed class SignatureSuppressionHandler(TimeProvider clock, IPilotSignatureSuppression suppression)
    : IModuleEffectSource,
        IDisposable
{
    internal const string EffectName = "targetSpectrumBreakerBonus";
    internal const string Graphic = "effects.Invulnerability";

    private sealed record Cycle(long PilotId, long ShipId, SignatureSuppressor Module, SignatureSuppressionSchedule Schedule);

    private readonly Dictionary<long, Cycle> _cycles = [];

    private long Now() => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static DogmaActiveEffect Effect(Cycle cycle) =>
        new(
            cycle.Module.ItemId,
            cycle.PilotId,
            cycle.ShipId,
            null,
            DogmaEffect.SignatureSuppression,
            cycle.Schedule.StartedAt,
            cycle.Module.DurationMilliseconds,
            0
        );

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.ShipId == pilot.ShipId && cycle.Schedule.Active(Now()) ? Effect(cycle) : null;

    private void Notify(PilotCallContext context, Cycle cycle, bool active) =>
        context.Notifications.Add(new ShipEffectChanged(Effect(cycle), Now(), active, active, active ? null : Now()));

    private void Attributes(
        PilotCallContext context,
        Cycle cycle,
        IReadOnlyDictionary<DogmaAttributeId, double> values,
        double? capacitor = null
    )
    {
        List<ModuleAttributeChange> changes = [];
        if (values.TryGetValue(DogmaAttributeId.SignatureRadius, out double signature))
        {
            changes.Add(new(cycle.PilotId, new DogmaItemKey.Item(cycle.ShipId), DogmaAttributeId.SignatureRadius, Now(), signature));
        }
        if (capacitor is double charge)
        {
            changes.Add(new(cycle.PilotId, new DogmaItemKey.Item(cycle.ShipId), DogmaAttributeId.CapacitorCharge, Now(), charge));
        }
        changes.Add(
            new(
                cycle.PilotId,
                new DogmaItemKey.Item(cycle.Module.ItemId),
                DogmaAttributeId.NextActivationTime,
                Now(),
                cycle.Schedule.ReadyAt
            )
        );
        context.Notifications.Add(new ModuleAttributesChanged(changes));
    }

    private static SignatureSuppressionContact Contact(Cycle cycle) => new(cycle.Module, cycle.Schedule);

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        var values = suppression.Stop(cycle.PilotId, cycle.ShipId, cycle.Module.ItemId);
        _cycles.Remove(cycle.Module.ItemId);
        if (context.Pilot.ShipId == cycle.ShipId)
        {
            context.Flight.SetSignatureSuppressionEffect(Contact(cycle), false);
        }
        Notify(context, cycle, false);
        Attributes(context, cycle, values);
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        foreach (var cycle in _cycles.Values.ToArray())
        {
            if (
                cycle.ShipId != context.Pilot.ShipId
                || !context.Flight.CanUseSelfModules
                || context.Flight.InWarp
                || !cycle.Schedule.Active(Now())
                || !suppression.Active(cycle.PilotId, cycle.ShipId, cycle.Module.ItemId, Now())
            )
            {
                Stop(context, cycle);
            }
        }
    }

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
    }

    public PilotCallResult Call(PilotCallContext context, string service, string method, object?[] args)
    {
        if (
            service != "dogmaIM"
            || args.Length < 2
            || BlueMarshal.Text(args[1]) != EffectName
            || (method, args) is not ("Activate", [long, _, _, long]) and not ("Deactivate", [long, _])
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            context.Flight.Checkpoint();
            Advance(context);
            long module = (long)args[0]!;
            if (method == "Deactivate")
            {
                // A one-shot module finishes its paid interval and never repeats.
                return PilotCallResult.None;
            }
            if (
                !context.Flight.CanUseSelfModules
                || context.Flight.InWarp
                || args[2] is not null && !Equals(args[2], context.Pilot.ShipId)
                || (long)args[3]! is < 0 or > int.MaxValue
            )
            {
                return new PilotCallResult.Failure("Activate signature suppression on your own battleship outside warp.");
            }
            if (_cycles.ContainsKey(module))
            {
                return PilotCallResult.None;
            }
            var result = suppression.Activate(context.Pilot.PilotId, module, Now());
            Cycle cycle = new(context.Pilot.PilotId, context.Pilot.ShipId, result.Module, result.Schedule);
            _cycles[module] = cycle;
            Notify(context, cycle, true);
            context.Flight.SetSignatureSuppressionEffect(Contact(cycle), true);
            Attributes(context, cycle, result.ShipAttributes, result.Capacitor);
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    public void Dispose()
    {
        foreach (var cycle in _cycles.Values)
        {
            suppression.Stop(cycle.PilotId, cycle.ShipId, cycle.Module.ItemId);
        }
        _cycles.Clear();
    }
}
