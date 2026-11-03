using System.Security.Cryptography;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Scoped)]
internal sealed class CombatHandler(TimeProvider clock, IPilotCombat combat, TargetingHandler targeting)
{
    private sealed record Cycle(
        long ShipId,
        CombatWeapon Weapon,
        long Target,
        long Started,
        long StartTime,
        long Repeats,
        bool Stopping = false
    );

    private readonly Dictionary<long, Cycle> _cycles = [];
    private const int ProbabilityResolution = 1_000_000;

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
    }

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.ShipId == pilot.ShipId
            ? new(
                module,
                pilot.PilotId,
                pilot.ShipId,
                cycle.Target,
                (DogmaEffect)cycle.Weapon.EffectId,
                cycle.StartTime,
                cycle.Weapon.CycleMilliseconds,
                cycle.Repeats
            )
            : null;

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        foreach (var (module, cycle) in _cycles.ToArray())
        {
            if (
                cycle.ShipId != context.Pilot.ShipId
                || !context.Flight.CanTarget
                || !targeting.IsLocked(cycle.Target)
                || context.Flight.FindTarget(cycle.Target) is null
            )
            {
                Stop(context, cycle);
                continue;
            }
            if (clock.GetElapsedTime(cycle.Started).TotalMilliseconds < cycle.Weapon.CycleMilliseconds)
            {
                continue;
            }
            Stop(context, cycle);
            if (cycle.Stopping || cycle.Repeats == 0)
            {
                continue;
            }
            try
            {
                Start(context, module, cycle.Target, cycle.Repeats - 1);
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException) { }
        }
    }

    private void Start(PilotCallContext context, long module, long target, long repeats, string? effectName = null)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var result = combat.Fire(
            context.Pilot.PilotId,
            module,
            target,
            context.Flight.Velocity,
            now,
            RandomNumberGenerator.GetInt32(ProbabilityResolution) / (double)ProbabilityResolution,
            effectName
        );
        var cycle = new Cycle(context.Pilot.ShipId, result.Weapon, target, clock.GetTimestamp(), now, repeats);
        _cycles.Add(module, cycle);
        context.NotificationsBeforeFlight.Add(new DamageMessageNotification(CombatMessages.Outgoing(result, target)));
        Effect(context, cycle, true);
        context.Flight.SetWeaponEffect(Contact(cycle), true);
        context.Flight.NotifyHealth(result.Health);
        context.Flight.RefreshWrecks();
        context.Flight.RefreshNpcs();
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new ModuleAttributeChange(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.ShipId),
                    DogmaAttributeId.CapacitorCharge,
                    now,
                    result.Capacitor
                ),
            ])
        );
        if (result.Reward is { } reward)
        {
            context.Notifications.Add(
                new AccountBalanceChanged(context.Pilot.PilotId, reward.BalanceCents / (double)MarketDefinitions.CentsPerIsk)
            );
        }
        if (result.Magazine is { } magazine)
        {
            AmmunitionHandler.NotifyQuantity(context, magazine, magazine.Quantity, now);
        }
        if (result.Destroyed)
        {
            Stop(context, cycle);
        }
    }

    private static WeaponContact Contact(Cycle cycle) =>
        new(
            cycle.Weapon.ItemId,
            cycle.Weapon.TypeId,
            cycle.Weapon.ChargeTypeId,
            cycle.Target,
            cycle.StartTime,
            cycle.Weapon.CycleMilliseconds,
            cycle.Weapon.EffectId == (int)DogmaEffect.ProjectileFired ? "effects.ProjectileFired" : "effects.Laser"
        );

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        if (!_cycles.Remove(cycle.Weapon.ItemId))
        {
            return;
        }
        Effect(context, cycle, false);
        context.Flight.SetWeaponEffect(Contact(cycle), false);
    }

    private void Effect(PilotCallContext context, Cycle cycle, bool active)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        context.Notifications.Add(
            new ShipEffectChanged(
                new DogmaActiveEffect(
                    cycle.Weapon.ItemId,
                    context.Pilot.PilotId,
                    cycle.ShipId,
                    cycle.Target,
                    (DogmaEffect)cycle.Weapon.EffectId,
                    cycle.StartTime,
                    cycle.Weapon.CycleMilliseconds,
                    cycle.Repeats
                ),
                now,
                active,
                active,
                active ? null : now
            )
        );
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "dogmaIM" || method is not ("Activate" or "Deactivate"))
        {
            return PilotCallResult.Unsupported;
        }
        context.Flight.Checkpoint();
        targeting.Advance(context);
        Advance(context);
        try
        {
            if (
                method == "Activate"
                && args is [long module, var effect, long target, long repeats]
                && BlueMarshal.Text(effect) is CombatDefinitions.CivilianWeaponEffect or CombatDefinitions.HybridWeaponEffect
                && repeats is >= 0 and <= int.MaxValue
            )
            {
                if (!context.Flight.CanTarget || !targeting.IsLocked(target) || !context.Flight.VisibleNpcs.Any(n => n.Id == target))
                {
                    return new PilotCallResult.Failure("Lock a nearby NPC before activating the weapon.");
                }
                if (_cycles.TryGetValue(module, out var current))
                {
                    return current.Target == target ? PilotCallResult.None : new PilotCallResult.Failure("The weapon is already active.");
                }
                Start(context, module, target, repeats, BlueMarshal.Text(effect));
                return PilotCallResult.None;
            }
            if (
                method == "Deactivate"
                && args is [long item, var effectName]
                && BlueMarshal.Text(effectName) is CombatDefinitions.CivilianWeaponEffect or CombatDefinitions.HybridWeaponEffect
            )
            {
                // Turret damage is paid at cycle start; stop repeating after the
                // current cycle. A reconnect cannot bypass the saved cooldown.
                if (_cycles.TryGetValue(item, out var stopping))
                {
                    _cycles[item] = stopping with { Stopping = true };
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
            return new PilotCallResult.Failure("The combat transaction exceeded the local value limit.");
        }
    }
}
