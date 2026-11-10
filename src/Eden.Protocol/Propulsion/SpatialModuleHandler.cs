using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Propulsion;

[Injectable(InjectionType.Scoped)]
internal sealed class SpatialModuleHandler(TimeProvider clock, IPilotSpatialModules modules, SharedTargetLocks locks)
    : IModuleEffectSource,
        IDisposable
{
    private sealed record Cycle(long PilotId, long ShipId, SpatialActivation Activation);

    private readonly Dictionary<long, Cycle> _cycles = [];

    private long Now() => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    internal static bool Handles(string name) => Effect(name) is not null;

    private static DogmaEffect? Effect(string name) =>
        name switch
        {
            "cloaking" => DogmaEffect.Cloaking,
            "cloakingPrototype" => DogmaEffect.PrototypeCloaking,
            "cloakingWarpSafe" => DogmaEffect.CovertCloaking,
            "microJumpDrive" => DogmaEffect.MicroJumpDrive,
            _ => null,
        };

    private static DogmaActiveEffect Active(Cycle cycle) =>
        new(
            cycle.Activation.Module.ItemId,
            cycle.PilotId,
            cycle.ShipId,
            null,
            cycle.Activation.Module.Effect,
            cycle.Activation.StartedAt,
            cycle.Activation.Module.Cloak ? null : cycle.Activation.Module.DurationMilliseconds,
            0
        );

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle)
        && cycle.ShipId == pilot.ShipId
        && (cycle.Activation.EndsAt == 0 || cycle.Activation.EndsAt > Now())
            ? Active(cycle)
            : null;

    internal bool BlocksModule(PilotCallContext context, bool offensive)
    {
        if (!context.Flight.Cloaked)
        {
            return false;
        }
        // Non-offensive modules retain the native five-second activation window.
        return offensive
            || !_cycles.Values.Any(c =>
                c.Activation.Module.Cloak
                && Now() < SpatialModuleRules.Deadline(c.Activation.StartedAt, SpatialModuleRules.ActivationGraceMilliseconds)
            );
    }

    private void Notify(PilotCallContext context, Cycle cycle, bool active) =>
        context.Notifications.Add(new ShipEffectChanged(Active(cycle), Now(), active, active, active ? null : Now()));

    private void Attributes(
        PilotCallContext context,
        Cycle cycle,
        IReadOnlyDictionary<DogmaAttributeId, double> values,
        double? capacitor = null
    )
    {
        var changes = values
            .Where(v => v.Key is DogmaAttributeId.MaxVelocity or DogmaAttributeId.SignatureRadius or DogmaAttributeId.ScanResolution)
            .Select(v => new ModuleAttributeChange(cycle.PilotId, new DogmaItemKey.Item(cycle.ShipId), v.Key, Now(), v.Value))
            .ToList();
        if (capacitor is { } charge)
        {
            changes.Add(new(cycle.PilotId, new DogmaItemKey.Item(cycle.ShipId), DogmaAttributeId.CapacitorCharge, Now(), charge));
        }
        double readyAt = values.GetValueOrDefault(DogmaAttributeId.NextActivationTime, cycle.Activation.ReadyAt);
        if (readyAt > 0)
        {
            changes.Add(
                new(
                    cycle.PilotId,
                    new DogmaItemKey.Item(cycle.Activation.Module.ItemId),
                    DogmaAttributeId.NextActivationTime,
                    Now(),
                    readyAt
                )
            );
        }
        context.Notifications.Add(new ModuleAttributesChanged(changes));
        if (context.Pilot.ShipId == cycle.ShipId && values.Count > 0)
        {
            context.Flight.RefreshFitting(values);
        }
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        var values = modules.Stop(cycle.PilotId, cycle.ShipId, cycle.Activation.Module.ItemId, Now());
        _cycles.Remove(cycle.Activation.Module.ItemId);
        if (context.Pilot.ShipId == cycle.ShipId)
        {
            context.Flight.SpatialEffect(cycle.Activation, false);
        }
        Notify(context, cycle, false);
        Attributes(context, cycle, values);
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected || _cycles.Count == 0)
        {
            return;
        }
        context.Flight.Checkpoint();
        foreach (var cycle in _cycles.Values.ToArray())
        {
            var activation = cycle.Activation;
            if (
                context.Pilot.ShipId != cycle.ShipId
                || !context.Flight.CanUseSelfModules
                || context.Flight.InWarp && !activation.Module.WarpSafe
                || !modules.Available(cycle.PilotId, cycle.ShipId, activation.Module.ItemId)
            )
            {
                Stop(context, cycle);
                continue;
            }
            if (activation.Module.Cloak)
            {
                if (!context.Flight.Cloaked || context.Flight.HasCloakObstacle())
                {
                    Stop(context, cycle);
                }
                continue;
            }
            if (activation.EndsAt <= Now())
            {
                var jump = modules.CompleteJump(cycle.PilotId, cycle.ShipId, activation.Module.ItemId, activation.StartedAt, Now());
                if (jump is not null)
                {
                    context.Flight.AdoptMicroJump(jump);
                }
                Stop(context, cycle);
            }
        }
    }

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_cycles.TryGetValue(module, out var cycle))
        {
            Stop(context, cycle);
        }
    }

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
    }

    internal PilotCallResult Call(PilotCallContext context, string method, object?[] args)
    {
        if (
            args.Length < 2
            || Effect(BlueMarshal.Text(args[1])) is not { } effect
            || (method, args) is not ("Activate", [long, _, _, long]) and not ("Deactivate", [long, _])
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            Advance(context);
            long module = (long)args[0]!;
            if (method == "Deactivate")
            {
                if (_cycles.TryGetValue(module, out var stopped) && stopped.Activation.Module.Cloak)
                {
                    Stop(context, stopped);
                }
                return PilotCallResult.None;
            }
            context.Flight.Checkpoint();
            if (
                !context.Flight.CanUseSelfModules
                || (context.Flight.InWarp || context.Flight.PreparingWarp) && effect != DogmaEffect.CovertCloaking
                || args[2] is not null && !Equals(args[2], context.Pilot.ShipId)
                || (long)args[3]! is < 0 or > int.MaxValue
            )
            {
                throw new InvalidDataException("Activate your spatial module outside warp and session transitions.");
            }
            if (SpatialModuleRules.IsCloak(effect) && context.Flight.HasCloakObstacle())
            {
                throw new InvalidDataException("Move at least 2 km clear of nearby objects before cloaking.");
            }
            var activated = modules.Activate(context.Pilot.PilotId, module, effect, context.Flight.Heading, context.Flight.Velocity, Now());
            Cycle cycle = new(context.Pilot.PilotId, context.Pilot.ShipId, activated);
            _cycles[module] = cycle;
            if (activated.Module.Cloak)
            {
                locks.RemoveShip(cycle.ShipId);
            }
            Notify(context, cycle, true);
            Attributes(context, cycle, activated.ShipAttributes, activated.Capacitor);
            context.Flight.SpatialEffect(activated, true);
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
            modules.Stop(cycle.PilotId, cycle.ShipId, cycle.Activation.Module.ItemId, Now());
        }
        _cycles.Clear();
    }
}
