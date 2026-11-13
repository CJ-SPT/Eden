using System.Security.Cryptography;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Scoped)]
internal sealed class CombatHandler(
    TimeProvider clock,
    IPilotCombat combat,
    TargetingHandler targeting,
    CharacterInteractionHistoryHandler? history = null
) : IModuleEffectSource
{
    private sealed record Cycle(
        long ShipId,
        CombatWeapon Weapon,
        long Target,
        long Started,
        long StartTime,
        long Repeats,
        IReadOnlyList<CombatWeapon> Weapons,
        bool Stopping = false,
        int PaidCycles = 0,
        IReadOnlyList<long>? ArcTargets = null
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

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_cycles.Values.FirstOrDefault(c => c.Weapons.Any(w => w.ItemId == module)) is { } cycle)
        {
            Stop(context, cycle);
        }
    }

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.Values.FirstOrDefault(c => c.Weapons.Any(w => w.ItemId == module)) is { } cycle && cycle.ShipId == pilot.ShipId
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
        bool refreshContacts = false;
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
            if (!context.Flight.Cloaked && !cycle.Stopping && cycle.Repeats > 0)
            {
                try
                {
                    var shot = Start(context, module, cycle.Target, cycle.Repeats - 1, previous: cycle, refreshContacts: false);
                    refreshContacts |= !shot.Destroyed;
                    continue;
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException) { }
            }
            Stop(context, cycle);
        }
        if (refreshContacts)
        {
            RefreshContacts(context);
        }
    }

    private CombatShot Start(
        PilotCallContext context,
        long module,
        long target,
        long repeats,
        string? effectName = null,
        Cycle? previous = null,
        bool refreshContacts = true
    )
    {
        long started = clock.GetTimestamp();
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var activation = effectName switch
        {
            CombatWireDefinitions.DisintegratorEffect => WeaponActivation.Disintegrator,
            CombatWireDefinitions.VortonEffect => WeaponActivation.Vorton,
            CombatWireDefinitions.AmmoWeaponEffect => WeaponActivation.AmmoFed,
            CombatWireDefinitions.CivilianWeaponEffect => WeaponActivation.Laser,
            _ => previous is null ? (WeaponActivation?)null : AdvancedWeaponRules.Activation((DogmaEffect)previous.Weapon.EffectId),
        };
        double roll = RandomNumberGenerator.GetInt32(ProbabilityResolution) / (double)ProbabilityResolution;
        var playerTarget = context.Flight.FindPilotWeaponTarget(target);
        var advanced = activation switch
        {
            WeaponActivation.Disintegrator => new AdvancedWeaponCycle(
                WeaponActivation.Disintegrator,
                previous is null ? 0 : previous.PaidCycles + 1,
                previous?.Weapon.ChargeTypeId
            ),
            WeaponActivation.Vorton => context.Flight.VortonCandidates(),
            _ => null,
        };
        var volley =
            advanced is not null
                ? combat.FireAdvanced(context.Pilot.PilotId, module, target, playerTarget, context.Flight.Velocity, now, roll, advanced)
            : playerTarget is not null
                ? combat.FirePlayerBank(context.Pilot.PilotId, module, playerTarget, context.Flight.Velocity, now, roll, activation)
            : combat.FireBank(context.Pilot.PilotId, module, target, context.Flight.Velocity, now, roll, activation);
        var result = volley.Shot;
        if (result.Destroyed || volley.SecondaryHits.Any(hit => hit.Destroyed))
        {
            history?.NotifyDestruction(context);
        }
        var cycle = new Cycle(
            context.Pilot.ShipId,
            result.Weapon,
            target,
            started,
            now,
            repeats,
            volley.Weapons,
            PaidCycles: result.SpoolCycles,
            ArcTargets: activation == WeaponActivation.Vorton ? volley.SecondaryHits.Select(hit => hit.Health.ShipId).ToArray() : null
        );
        _cycles[result.Weapon.ItemId] = cycle;
        context.NotificationsBeforeFlight.Add(
            new DamageMessageNotification(CombatMessages.Outgoing(result, target) with { Banked = volley.Weapons.Count > 1 })
        );
        if (previous is null)
        {
            Effect(context, cycle, true);
            foreach (var weapon in cycle.Weapons)
            {
                context.Flight.SetWeaponEffect(Contact(cycle with { Weapon = weapon }), true);
            }
        }
        else
        {
            foreach (var weapon in cycle.Weapons)
            {
                context.Flight.RepeatWeaponEffect(Contact(previous with { Weapon = weapon }), Contact(cycle with { Weapon = weapon }));
            }
        }
        context.Flight.NotifyHealth(result.Health);
        foreach (var hit in volley.SecondaryHits)
        {
            context.NotificationsBeforeFlight.Add(
                new DamageMessageNotification(CombatMessages.Outgoing(hit, hit.Health.ShipId) with { Banked = volley.Weapons.Count > 1 })
            );
            context.Flight.NotifyHealth(hit.Health);
        }
        if (volley.SecondaryHits.Count != 0)
        {
            context.Flight.RefreshDrones();
        }
        if (refreshContacts || result.Destroyed || volley.SecondaryHits.Any(hit => hit.Destroyed))
        {
            RefreshContacts(context);
        }
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
        if ((volley.SecondaryHits.LastOrDefault(hit => hit.Reward is not null)?.Reward ?? result.Reward) is { } reward)
        {
            context.Notifications.Add(
                new AccountBalanceChanged(context.Pilot.PilotId, reward.BalanceCents / (double)MarketDefinitions.CentsPerIsk)
            );
        }
        foreach (var magazine in volley.Magazines)
        {
            MagazineWire.NotifyState(context, magazine, now);
        }
        if (result.Destroyed)
        {
            Stop(context, cycle);
        }
        return result;
    }

    private static void RefreshContacts(PilotCallContext context)
    {
        context.Flight.RefreshWrecks();
        context.Flight.RefreshNpcs();
    }

    private static WeaponContact Contact(Cycle cycle) =>
        new(
            cycle.Weapon.ItemId,
            cycle.Weapon.TypeId,
            cycle.Weapon.ChargeTypeId,
            cycle.Target,
            cycle.StartTime,
            cycle.Weapon.CycleMilliseconds,
            (DogmaEffect)cycle.Weapon.EffectId switch
            {
                DogmaEffect.ProjectileFired => "effects.ProjectileFired",
                DogmaEffect.Disintegrator => "effects.TriglavianBeam",
                DogmaEffect.Vorton => cycle.Weapon.Vorton?.Size switch
                {
                    1 => "effects.UpwellChainWeaponSmall",
                    2 => "effects.UpwellChainWeaponMedium",
                    _ => "effects.UpwellChainWeaponLarge",
                },
                _ => "effects.Laser",
            },
            cycle.ArcTargets,
            cycle.Weapon.Spool
        );

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        if (!_cycles.Remove(cycle.Weapon.ItemId))
        {
            return;
        }
        Effect(context, cycle, false);
        foreach (var weapon in cycle.Weapons)
        {
            context.Flight.SetWeaponEffect(Contact(cycle with { Weapon = weapon }), false);
        }
    }

    private void Effect(PilotCallContext context, Cycle cycle, bool active)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        foreach (var weapon in cycle.Weapons)
        {
            context.Notifications.Add(
                new ShipEffectChanged(
                    new DogmaActiveEffect(
                        weapon.ItemId,
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
                && CombatWireDefinitions.Handles(BlueMarshal.Text(effect))
                && repeats is >= 0 and <= int.MaxValue
            )
            {
                if (
                    !context.Flight.CanTarget
                    || !targeting.IsLocked(target)
                    || !context.Flight.VisibleNpcs.Any(n => n.Id == target) && context.Flight.FindPilotWeaponTarget(target) is null
                )
                {
                    return new PilotCallResult.Failure("Lock a nearby ship before activating the weapon.");
                }
                if (_cycles.Values.FirstOrDefault(c => c.Weapons.Any(w => w.ItemId == module)) is { } current)
                {
                    return current.Target == target ? PilotCallResult.None : new PilotCallResult.Failure("The weapon is already active.");
                }
                Start(context, module, target, repeats, BlueMarshal.Text(effect));
                return PilotCallResult.None;
            }
            if (
                method == "Deactivate"
                && args is [long item, var effectName]
                && CombatWireDefinitions.Handles(BlueMarshal.Text(effectName))
            )
            {
                // Turret damage is paid at cycle start; stop repeating after the
                // current cycle. A reconnect cannot bypass the saved cooldown.
                if (_cycles.Values.FirstOrDefault(c => c.Weapons.Any(w => w.ItemId == item)) is { } stopping)
                {
                    _cycles[stopping.Weapon.ItemId] = stopping with { Stopping = true };
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
