using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Propulsion;

[Injectable(InjectionType.Scoped)]
internal sealed class InterdictionHandler(
    IPilotInterdiction interdiction,
    TimeProvider clock,
    TargetingHandler targeting,
    IPilotFitting fitting
) : IModuleEffectSource
{
    internal const string NullifierEffectName = "interdictionNullifierBonus";

    private sealed record Cycle(
        DogmaActiveEffect Effect,
        InterdictionActivation Activation,
        WeaponContact? Contact = null,
        TackleTarget? Target = null,
        long Repeats = 0,
        bool Stopping = false
    );

    private readonly Dictionary<long, Cycle> _cycles = [];
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.Effect.ShipId == pilot.ShipId ? cycle.Effect : null;

    private static DogmaActiveEffect Effect(DockedPilot pilot, InterdictionActivation cycle) =>
        new(
            cycle.ModuleId,
            pilot.PilotId,
            pilot.ShipId,
            cycle.TargetId,
            cycle.Effect,
            cycle.ExpiresAt - (long)Math.Ceiling(cycle.Duration * TimeSpan.TicksPerMillisecond),
            cycle.Duration,
            0
        );

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_cycles.TryGetValue(module, out var cycle))
        {
            if (cycle.Contact is not null)
            {
                interdiction.Stop(cycle.Effect.PilotId, cycle.Effect.ShipId, module, Now);
                context.Flight.SetWeaponEffect(cycle.Contact, false);
                NotifyAttributes(context);
            }
            _cycles.Remove(module);
            context.Notifications.Add(new ShipEffectChanged(cycle.Effect, Now, false, false, Now));
        }
    }

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        foreach (var (module, cycle) in _cycles.ToArray())
        {
            if (
                cycle.Effect.ShipId != context.Pilot.ShipId
                || !context.Flight.CanUseSelfModules
                || cycle.Contact is not null
                    && cycle.Effect.Effect != DogmaEffect.InterdictionNullifier
                    && (
                        !context.Flight.CanTarget
                        || cycle.Target is not null
                            && (!targeting.IsLocked(cycle.Target.Id) || context.Flight.FindTarget(cycle.Target.Id) is null)
                    )
            )
            {
                CancelModule(context, module);
                continue;
            }
            if (Now >= cycle.Activation.ExpiresAt)
            {
                if (cycle.Contact is not null && !cycle.Stopping && cycle.Repeats > 0)
                {
                    try
                    {
                        StartGenerator(context, module, cycle.Activation.Effect, cycle.Target, cycle.Repeats - 1, cycle);
                        continue;
                    }
                    catch (Exception error) when (error is InvalidDataException or OverflowException) { }
                }
                CancelModule(context, module);
            }
            else if (cycle.Contact is not null && !interdiction.Active(cycle.Effect.ShipId, module, Now))
            {
                CancelModule(context, module);
            }
        }
        context.Flight.RefreshInterdiction();
    }

    internal static DogmaEffect? Effect(string name) =>
        name switch
        {
            NullifierEffectName => DogmaEffect.InterdictionNullifier,
            "warpDisruptSphere" => DogmaEffect.WarpDisruptSphere,
            "shipModuleFocusedWarpDisruptionScript" => DogmaEffect.FocusedWarpDisruption,
            "shipModuleFocusedWarpScramblingScript" => DogmaEffect.FocusedWarpScrambling,
            _ => null,
        };

    internal void Cancel(PilotCallContext context)
    {
        foreach (long module in _cycles.Keys.ToArray())
        {
            CancelModule(context, module);
        }
    }

    internal PilotCallResult Call(PilotCallContext context, string method, DogmaEffect effect, object?[] args)
    {
        if (method == "Deactivate" && args is [long stopping, _])
        {
            if (_cycles.TryGetValue(stopping, out var cycle))
            {
                _cycles[stopping] = cycle with { Stopping = true };
            }
            return PilotCallResult.None;
        }
        if (method != "Activate" || args is not [long module, _, var id, long repeats])
        {
            return PilotCallResult.Unsupported;
        }
        if (effect == DogmaEffect.InterdictionNullifier)
        {
            if (
                !context.Flight.CanUseSelfModules
                || id is not null && !Equals(id, context.Pilot.ShipId)
                || repeats is < 0 or > int.MaxValue
            )
            {
                return new PilotCallResult.Failure("Activate the interdiction nullifier on your own ship in space.");
            }
            if (_cycles.ContainsKey(module))
            {
                return PilotCallResult.None;
            }
            try
            {
                context.Flight.Checkpoint();
                StartNullifier(context, module);
                return PilotCallResult.None;
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException)
            {
                return new PilotCallResult.Failure(error.Message);
            }
        }
        if (!context.Flight.CanTarget || repeats is < 0 or > int.MaxValue || id is not (null or long))
        {
            return new PilotCallResult.Failure("Use an unscripted field without a target or a focused script with a locked target.");
        }
        if (_cycles.ContainsKey(module))
        {
            return PilotCallResult.None;
        }
        try
        {
            context.Flight.Checkpoint();
            TackleTarget? target = id is long targetId
                ? new(targetId, context.Flight.VisibleNpcs.Any(npc => npc.Id == targetId) ? TackleTargetKind.Npc : TackleTargetKind.Ship)
                : null;
            StartGenerator(context, module, effect, target, repeats);
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private void StartGenerator(
        PilotCallContext context,
        long module,
        DogmaEffect effect,
        TackleTarget? target,
        long repeats,
        Cycle? previous = null
    )
    {
        var activation = interdiction.ActivateGenerator(
            context.Pilot.PilotId,
            context.Pilot.ShipId,
            module,
            effect,
            target,
            target is not null && targeting.IsLocked(target.Id) ? new HashSet<long> { target.Id } : [],
            Now
        );
        // The module button tracks warpDisruptSphere even when the loaded script supplies the tackle effect.
        var active = Effect(context.Pilot, activation) with
        {
            Effect = DogmaEffect.WarpDisruptSphere,
            Repeats = repeats,
        };
        bool field = activation.Effect == DogmaEffect.WarpDisruptSphere;
        var old = previous?.Contact;
        // Keep the same visual in the observer snapshot when a new cycle pays for the same field.
        WeaponContact contact =
            field && old is not null && old.FieldRange == activation.Range
                ? old
                : new(
                    module,
                    activation.TypeId,
                    0,
                    target?.Id,
                    active.StartTime,
                    activation.Duration,
                    field ? "effects.WarpDisruptFieldGenerating" : TackleWire.Graphic(activation.Effect),
                    FieldRange: field ? activation.Range : null
                );
        _cycles[module] = new(active, activation, contact, target, repeats);
        if (old is null)
        {
            context.Notifications.Add(new ShipEffectChanged(active, Now, true, true));
            context.Flight.SetWeaponEffect(contact, true);
        }
        else if (!ReferenceEquals(old, contact))
        {
            context.Flight.RepeatWeaponEffect(old, contact);
        }
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.ShipId),
                    DogmaAttributeId.CapacitorCharge,
                    Now,
                    activation.Capacitor
                ),
            ])
        );
        NotifyAttributes(context);
        context.Flight.RefreshInterdiction();
    }

    private void StartNullifier(PilotCallContext context, long module)
    {
        var activation = interdiction.ActivateNullifier(context.Pilot.PilotId, context.Pilot.ShipId, module, Now);
        var active = Effect(context.Pilot, activation);
        WeaponContact contact = new(
            module,
            activation.TypeId,
            0,
            null,
            active.StartTime,
            activation.Duration,
            "effects.Invulnerability",
            Offensive: false
        );
        _cycles[module] = new(active, activation, contact);
        context.Notifications.Add(new ShipEffectChanged(active, Now, true, true));
        context.Flight.SetWeaponEffect(contact, true);
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.ShipId),
                    DogmaAttributeId.CapacitorCharge,
                    Now,
                    activation.Capacitor
                ),
                new(context.Pilot.PilotId, new DogmaItemKey.Item(module), DogmaAttributeId.NextActivationTime, Now, activation.ReadyAt),
            ])
        );
        NotifyAttributes(context);
    }

    private void NotifyAttributes(PilotCallContext context)
    {
        var attributes = fitting.ShipAttributes(context.Pilot.PilotId);
        context.Notifications.Add(
            new ModuleAttributesChanged(
                new[]
                {
                    DogmaAttributeId.SignatureRadius,
                    DogmaAttributeId.DisallowAssistance,
                    DogmaAttributeId.WarpBubbleImmune,
                    DogmaAttributeId.MaxTargetRange,
                    DogmaAttributeId.ScanResolution,
                    DogmaAttributeId.DroneBandwidth,
                }
                    .Select(attribute => new ModuleAttributeChange(
                        context.Pilot.PilotId,
                        new DogmaItemKey.Item(context.Pilot.ShipId),
                        attribute,
                        Now,
                        attributes.GetValueOrDefault(attribute)
                    ))
                    .ToArray()
            )
        );
    }

    internal PilotCallResult? CallLauncher(PilotCallContext context, string method, object?[] args)
    {
        if (args[0] is not long module || !interdiction.IsLauncher(context.Pilot.PilotId, module))
        {
            return null;
        }
        if (method == "Deactivate")
        {
            return PilotCallResult.None;
        }
        if (args is not [_, _, null, long repeats] || repeats < 0 || !context.Flight.CanTarget)
        {
            return new PilotCallResult.Failure("Launch an interdiction probe without a target outside warp.");
        }
        try
        {
            context.Flight.Checkpoint();
            var cycle = interdiction.Launch(context.Pilot.PilotId, context.Pilot.ShipId, module, Now);
            _cycles[module] = new(Effect(context.Pilot, cycle), cycle);
            context.Notifications.Add(new ShipEffectChanged(Effect(context.Pilot, cycle), Now, true, true));
            if (cycle.Magazine?.Current is { } magazine)
            {
                MagazineWire.NotifyQuantity(context, magazine, magazine.Quantity, Now);
            }
            context.Notifications.Add(
                new ModuleAttributesChanged([
                    new(
                        context.Pilot.PilotId,
                        new DogmaItemKey.Item(context.Pilot.ShipId),
                        DogmaAttributeId.CapacitorCharge,
                        Now,
                        cycle.Capacitor
                    ),
                ])
            );
            context.Flight.RefreshInterdiction();
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
