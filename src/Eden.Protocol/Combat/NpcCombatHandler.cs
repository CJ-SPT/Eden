using System.Security.Cryptography;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Scoped)]
internal sealed class NpcCombatHandler(
    TimeProvider clock,
    INpcCombat combat,
    SharedTargetLocks locks,
    ShipLossCoordinator shipLoss,
    SharedSpace space,
    INpcMissiles? missiles = null,
    CharacterInteractionHistoryHandler? history = null,
    NpcEwarHandler? ewar = null
) : IDisposable
{
    private const int ProbabilityResolution = 1_000_000;
    private long _shipId;
    private bool _disengaged = true;
    private long[] _aggressors = [];
    private long _nextAcquire;
    private int _acquireSystem;
    private NpcTargetCandidate[] _candidates = [];

    public void Enter(PilotCallContext context)
    {
        // Reconnecting starts without an offline fight or overdue burst. Local
        // detection can establish a fresh fight with a new reaction interval.
        _nextAcquire = 0;
        _candidates = [];
        combat.Disengage(context.Pilot.ShipId);
        _shipId = context.Pilot.ShipId;
        _disengaged = true;
    }

    private void Clear()
    {
        foreach (long npc in _aggressors)
        {
            locks.Remove(npc, _shipId);
        }
        if (_shipId != 0 && !_disengaged)
        {
            combat.Disengage(_shipId);
        }
        _nextAcquire = 0;
        _candidates = [];
        _aggressors = [];
        _disengaged = true;
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        if (_shipId != context.Pilot.ShipId)
        {
            Clear();
            _shipId = context.Pilot.ShipId;
            _disengaged = false;
        }
        if (!context.Flight.CanTarget || context.Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup)
        {
            Clear();
            return;
        }
        _disengaged = false;
        long acquiredAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var candidates = space.NpcTargets(context.Pilot.SystemId).OrderBy(c => c.ShipId).ToArray();
        bool refresh = acquiredAt >= _nextAcquire || _acquireSystem != context.Pilot.SystemId || !_candidates.SequenceEqual(candidates);
        if (refresh)
        {
            combat.Acquire(candidates, acquiredAt);
            _nextAcquire = checked(acquiredAt + TimeSpan.TicksPerSecond);
            _acquireSystem = context.Pilot.SystemId;
            _candidates = candidates;
        }
        ewar?.Advance(context);
        foreach (long npc in combat.Aggressors(_shipId))
        {
            if (!context.Flight.VisibleNpcs.Any(n => n.Id == npc))
            {
                // A dispatched response keeps its target while travelling from
                // an off-grid origin. Visibility alone must not cancel its alarm.
                if (!context.Flight.NpcInWarp(npc))
                {
                    combat.Disengage(_shipId, npc);
                }
                continue;
            }
            if (context.Flight.NpcInWarp(npc))
            {
                continue;
            }
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            var shot = combat.Fire(
                context.Pilot.PilotId,
                _shipId,
                npc,
                context.Flight.Velocity,
                now,
                RandomNumberGenerator.GetInt32(ProbabilityResolution) / (double)ProbabilityResolution,
                context.Flight.CurrentStamp
            );
            if (shot is null)
            {
                continue;
            }
            refresh = true;
            Notify(context, shot);
            if (shot.Capsule is not null)
            {
                break;
            }
        }
        if (missiles is not null && context.Flight.CanTarget && context.Pilot.ShipGroupId != FleetDefinitions.CapsuleGroup)
        {
            foreach (
                var impact in missiles.Advance(
                    context.Pilot.PilotId,
                    _shipId,
                    context.Flight.Velocity,
                    clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
                )
            )
            {
                refresh = true;
                Notify(context, impact);
            }
            context.Flight.RefreshMissiles();
        }
        var visible = context.Flight.VisibleNpcs.Where(n => n.Warp is null).Select(n => n.Id).ToHashSet();
        var current = combat.Aggressors(_shipId).Where(visible.Contains).ToArray();
        foreach (long npc in _aggressors.Except(current))
        {
            locks.Remove(npc, _shipId);
        }
        foreach (long npc in current.Except(_aggressors))
        {
            locks.Add(npc, _shipId);
        }
        _aggressors = current;
        if (refresh)
        {
            context.Flight.RefreshNpcs();
        }
    }

    private void Notify(PilotCallContext context, NpcShot shot)
    {
        context.NotificationsBeforeFlight.Add(new DamageMessageNotification(CombatMessages.Incoming(shot)));
        if (!shot.IsMissile)
        {
            context.Flight.NotifyNpcWeapon(shot.NpcId, shot.Weapon);
        }
        context.Flight.NotifyHealth(shot.Health);
        if (shot.Health.ShipId != context.Pilot.ShipId)
        {
            context.Flight.RefreshDrones();
        }
        context.Notifications.Add(
            new ModuleAttributesChanged([
                Attribute(DogmaAttributeId.ShieldCharge, shot.Health.Remaining.Shield),
                Attribute(DogmaAttributeId.ArmorDamage, shot.Health.Defense.Armor - shot.Health.Remaining.Armor),
                Attribute(DogmaAttributeId.Damage, shot.Health.Defense.Structure - shot.Health.Remaining.Structure),
            ])
        );
        if (shot.Capsule is { } capsule)
        {
            history?.NotifyDestruction(context);
            if (shot.Insurance is { } payout)
            {
                context.Notifications.Add(
                    new AccountBalanceChanged(context.Pilot.PilotId, (double)payout.BalanceCents / MarketDefinitions.CentsPerIsk)
                );
            }
            shipLoss.Replace(context, capsule);
            Clear();
            _shipId = capsule.ShipId;
            return;
        }

        ModuleAttributeChange Attribute(DogmaAttributeId attribute, double value) =>
            new(
                context.Pilot.PilotId,
                new DogmaItemKey.Item(shot.Health.ShipId),
                attribute,
                clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                value
            );
    }

    public void Leave()
    {
        Clear();
        _shipId = 0;
    }

    public void Dispose() => Leave();
}
