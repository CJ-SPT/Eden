using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Scoped)]
internal sealed class MissileHandler(
    TimeProvider clock,
    IPilotMissiles missiles,
    TargetingHandler targeting,
    CharacterInteractionHistoryHandler? history = null
) : IModuleEffectSource, IDisposable
{
    internal const string EffectName = "useMissiles";

    private sealed record Cycle(
        long ShipId,
        MissileLauncher Launcher,
        long Target,
        long Started,
        long StartTime,
        long Repeats,
        IReadOnlyList<MissileLauncher> Launchers,
        bool Stopping = false
    );

    private readonly Dictionary<long, Cycle> _cycles = [];
    private long _pilotId;
    private long _shipId;

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.Values.FirstOrDefault(c => c.Launchers.Any(l => l.ItemId == module)) is { } cycle && cycle.ShipId == pilot.ShipId
            ? Effect(pilot.PilotId, cycle with { Launcher = cycle.Launchers.Single(l => l.ItemId == module) })
            : null;

    private static DogmaActiveEffect Effect(long pilot, Cycle cycle) =>
        new(
            cycle.Launcher.ItemId,
            pilot,
            cycle.ShipId,
            cycle.Target,
            DogmaEffect.UseMissiles,
            cycle.StartTime,
            cycle.Launcher.CycleMilliseconds,
            cycle.Repeats
        );

    private void Notify(PilotCallContext context, Cycle cycle, bool active)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        foreach (var launcher in cycle.Launchers)
        {
            context.Notifications.Add(
                new ShipEffectChanged(
                    Effect(context.Pilot.PilotId, cycle with { Launcher = launcher }),
                    now,
                    active,
                    active,
                    active ? null : now
                )
            );
        }
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        if (_cycles.Remove(cycle.Launcher.ItemId))
        {
            Notify(context, cycle, false);
        }
    }

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
        if (_pilotId != 0 && _shipId != 0)
        {
            missiles.Cancel(_pilotId, _shipId);
        }
        _pilotId = 0;
        _shipId = 0;
    }

    private void Start(PilotCallContext context, long module, long? target, long repeats, bool continuing)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var locked = context.Flight.VisibleNpcs.Where(n => targeting.IsLocked(n.Id)).Select(n => n.Id).ToHashSet();
        if (target is long pilotTarget && targeting.IsLocked(pilotTarget) && context.Flight.FindPilotWeaponTarget(pilotTarget) is not null)
        {
            locked.Add(pilotTarget);
        }
        var launches = missiles.LaunchBank(
            context.Pilot.PilotId,
            module,
            target,
            locked,
            new(context.Flight.Velocity, context.Flight.Pilot.Flight.MaxSpeed, new(0, 0, -1), context.Flight.CurrentStamp),
            now
        );
        var result = launches[0];
        _pilotId = context.Pilot.PilotId;
        _shipId = context.Pilot.ShipId;
        Cycle cycle = new(
            _shipId,
            result.Launcher with
            {
                CycleMilliseconds = launches.Max(l => l.Launcher.CycleMilliseconds),
            },
            result.Projectile.TargetId,
            clock.GetTimestamp(),
            now,
            repeats,
            launches.Select(l => l.Launcher).ToArray()
        );
        _cycles[result.Launcher.ItemId] = cycle;
        if (!continuing)
        {
            Notify(context, cycle, true);
        }
        foreach (var launch in launches)
        {
            MagazineWire.NotifyState(context, launch.Magazine, now);
        }
        context.Flight.RefreshMissiles();
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        if (_shipId != 0 && (_shipId != context.Pilot.ShipId || !context.Flight.CanTarget))
        {
            Cancel(context);
        }
        if (!context.Flight.CanTarget)
        {
            return;
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        bool hadMissiles = context.Flight.VisibleMissiles.Count > 0;
        missiles.Advance(context.Pilot.SystemId, now);
        context.Flight.RefreshMissiles();
        var impacts = missiles.TakeImpacts(context.Pilot.PilotId);
        foreach (var impact in impacts)
        {
            if (impact.Health.Remaining.Destroyed)
            {
                history?.NotifyDestruction(context);
            }
            context.NotificationsBeforeFlight.Add(
                new DamageMessageNotification(
                    new OutgoingCombatMessage(
                        impact.Applied,
                        CombatHitQuality.Standard,
                        impact.TargetId,
                        impact.ChargeTypeId,
                        impact.Damage
                    )
                )
            );
            context.Flight.NotifyHealth(impact.Health);
            if (impact.Reward is { } reward)
            {
                context.Notifications.Add(
                    new AccountBalanceChanged(context.Pilot.PilotId, reward.BalanceCents / (double)MarketDefinitions.CentsPerIsk)
                );
            }
        }
        if (hadMissiles || context.Flight.VisibleMissiles.Count > 0 || impacts.Count > 0)
        {
            context.Flight.RefreshWrecks();
            context.Flight.RefreshNpcs();
        }
        foreach (var cycle in _cycles.Values.ToArray())
        {
            if (!cycle.Launcher.Automatic && (!targeting.IsLocked(cycle.Target) || context.Flight.FindTarget(cycle.Target) is null))
            {
                Stop(context, cycle);
                continue;
            }
            if (clock.GetElapsedTime(cycle.Started).TotalMilliseconds < cycle.Launcher.CycleMilliseconds)
            {
                continue;
            }
            if (!cycle.Stopping && cycle.Repeats > 0)
            {
                try
                {
                    Start(context, cycle.Launcher.ItemId, cycle.Launcher.Automatic ? null : cycle.Target, cycle.Repeats - 1, true);
                    continue;
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException) { }
            }
            Stop(context, cycle);
        }
    }

    public PilotCallResult Call(PilotCallContext context, string service, string method, object?[] args)
    {
        if (
            service != "dogmaIM"
            || method is not ("Activate" or "Deactivate")
            || args.Length < 2
            || BlueMarshal.Text(args[1]) != EffectName
        )
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
                && args is [long module, _, var target, long repeats]
                && (target is null or long)
                && repeats is >= 0 and <= int.MaxValue
            )
            {
                if (!context.Flight.CanTarget)
                {
                    return new PilotCallResult.Failure("Launch missiles while in space and outside warp.");
                }
                if (_cycles.Values.FirstOrDefault(c => c.Launchers.Any(l => l.ItemId == module)) is { } active)
                {
                    return !active.Launcher.Automatic && target is long requested && requested != active.Target
                        ? new PilotCallResult.Failure("The launcher is already active on another target.")
                        : PilotCallResult.None;
                }
                Start(context, module, target as long?, repeats, false);
                return PilotCallResult.None;
            }
            if (method == "Deactivate" && args is [long id, _])
            {
                if (_cycles.Values.FirstOrDefault(c => c.Launchers.Any(l => l.ItemId == id)) is { } cycle)
                {
                    _cycles[cycle.Launcher.ItemId] = cycle with { Stopping = true };
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
            return new PilotCallResult.Failure("The missile transaction exceeded the local value limit.");
        }
    }

    public void Dispose()
    {
        if (_pilotId != 0 && _shipId != 0)
        {
            missiles.Cancel(_pilotId, _shipId);
        }
    }
}
