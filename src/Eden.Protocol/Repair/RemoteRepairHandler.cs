using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Repair;

[Injectable(InjectionType.Scoped)]
internal sealed class RemoteRepairHandler(
    TimeProvider clock,
    IPilotRemoteRepairing repairing,
    TargetingHandler targeting,
    RemoteRepairSignal signal,
    IPilotHealth healthStore
) : IModuleEffectSource, IDisposable
{
    private sealed record Cycle(long PilotId, RemoteRepairCycle Repair, long Repeats, bool Stopping = false, bool FuelDepleted = false);

    private readonly Dictionary<long, Cycle> _cycles = [];
    private readonly Dictionary<long, RemoteRepairApplied> _received = [];
    private long _pilotId;
    private bool _subscribed;

    private long Now() => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public void Enter(PilotCallContext context)
    {
        lock (_received)
        {
            _pilotId = context.Pilot.PilotId;
        }
        if (!_subscribed)
        {
            signal.Applied += Receive;
            _subscribed = true;
        }
    }

    private void Receive(RemoteRepairApplied repair)
    {
        lock (_received)
        {
            if (repair.PilotId == _pilotId)
            {
                _received[repair.Health.ShipId] = repair;
            }
        }
    }

    private static DogmaActiveEffect Effect(Cycle cycle) =>
        new(
            cycle.Repair.Module.ItemId,
            cycle.PilotId,
            cycle.Repair.ShipId,
            cycle.Repair.TargetId,
            cycle.Repair.Module.Effect,
            cycle.Repair.StartedAt,
            cycle.Repair.Module.DurationMilliseconds,
            cycle.Repeats
        );

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.Repair.ShipId == pilot.ShipId ? Effect(cycle) : null;

    private void Notify(PilotCallContext context, Cycle cycle, bool active) =>
        context.Notifications.Add(new ShipEffectChanged(Effect(cycle), Now(), active, active, active ? null : Now()));

    private void Start(PilotCallContext context, long module, DogmaEffect effect, long target, long repeats, Cycle? previous = null)
    {
        if (context.Flight.FindTarget(target) is null)
        {
            throw new InvalidDataException("Lock another pilot's nearby ship before applying remote repairs.");
        }
        var result = repairing.Start(
            context.Pilot.PilotId,
            module,
            effect,
            target,
            targeting.IsLocked(target) ? new HashSet<long> { target } : [],
            Now()
        );
        Cycle cycle = new(context.Pilot.PilotId, result.Cycle, repeats, FuelDepleted: result.Magazine is { Quantity: 0 });
        _cycles[module] = cycle;
        if (previous is null)
        {
            Notify(context, cycle, true);
        }
        else
        {
            context.Flight.SetRemoteRepairEffect(previous.Repair, false);
        }
        context.Flight.SetRemoteRepairEffect(cycle.Repair, true);
        if (result.Magazine is { } magazine)
        {
            MagazineWire.NotifyQuantity(context, magazine, magazine.Quantity, Now());
        }
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.ShipId),
                    DogmaAttributeId.CapacitorCharge,
                    Now(),
                    result.Capacitor
                ),
            ])
        );
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        repairing.Stop(cycle.PilotId, cycle.Repair.Module.ItemId);
        _cycles.Remove(cycle.Repair.Module.ItemId);
        Notify(context, cycle, false);
        if (cycle.Repair.ShipId == context.Pilot.ShipId)
        {
            context.Flight.SetRemoteRepairEffect(cycle.Repair, false);
        }
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        Enter(context);
        foreach (var cycle in _cycles.Values.ToArray())
        {
            var repair = cycle.Repair;
            if (
                repair.ShipId != context.Pilot.ShipId
                || !context.Flight.CanTarget
                || context.Flight.InWarp
                || !targeting.IsLocked(repair.TargetId)
                || context.Flight.FindTarget(repair.TargetId) is null
                || !repairing.IsOnline(cycle.PilotId, repair.ShipId, repair.Module.ItemId)
            )
            {
                Stop(context, cycle);
                continue;
            }
            if (repair.CompletesAt > Now())
            {
                continue;
            }
            try
            {
                repairing.Complete(cycle.PilotId, repair.Module.ItemId, Now());
                if (!cycle.Stopping && !cycle.FuelDepleted && cycle.Repeats > 0)
                {
                    Start(context, repair.Module.ItemId, repair.Module.Effect, repair.TargetId, cycle.Repeats - 1, cycle);
                    continue;
                }
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException) { }
            Stop(context, cycle);
        }
        RemoteRepairApplied[] received;
        lock (_received)
        {
            received = _received.Values.ToArray();
            _received.Clear();
        }
        foreach (var repair in received.Where(r => r.Health.ShipId == context.Pilot.ShipId))
        {
            var health = healthStore.Read(repair.PilotId, Now());
            context.Flight.NotifyHealth(health);
            context.Notifications.Add(
                new ModuleAttributesChanged([
                    new(
                        context.Pilot.PilotId,
                        new DogmaItemKey.Item(health.ShipId),
                        DogmaAttributeId.ShieldCharge,
                        Now(),
                        health.Remaining.Shield
                    ),
                    new(
                        context.Pilot.PilotId,
                        new DogmaItemKey.Item(health.ShipId),
                        DogmaAttributeId.ArmorDamage,
                        Now(),
                        health.Defense.Armor - health.Remaining.Armor
                    ),
                    new(
                        context.Pilot.PilotId,
                        new DogmaItemKey.Item(health.ShipId),
                        DogmaAttributeId.Damage,
                        Now(),
                        health.Defense.Structure - health.Remaining.Structure
                    ),
                ])
            );
        }
    }

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
        Dispose();
    }

    public PilotCallResult Call(PilotCallContext context, string service, string method, object?[] args)
    {
        if (service != "dogmaIM" || args.Length < 2 || RemoteRepairWire.Effect(BlueMarshal.Text(args[1])) is not { } effect)
        {
            return PilotCallResult.Unsupported;
        }
        if ((method, args) is not ("Activate", [long, _, long, long]) and not ("Deactivate", [long, _]))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            context.Flight.Checkpoint();
            targeting.Advance(context);
            Advance(context);
            long module = (long)args[0]!;
            if (_cycles.TryGetValue(module, out var cycle))
            {
                if (cycle.Repair.Module.Effect != effect || method == "Activate" && (long)args[2]! != cycle.Repair.TargetId)
                {
                    return new PilotCallResult.Failure("The repairer is already active with a different effect or target.");
                }
                if (method == "Deactivate")
                {
                    _cycles[module] = cycle with { Stopping = true };
                }
                return PilotCallResult.None;
            }
            if (method == "Deactivate")
            {
                return PilotCallResult.None;
            }
            if (!context.Flight.CanTarget || context.Flight.InWarp || (long)args[3]! is < 0 or > int.MaxValue)
            {
                return new PilotCallResult.Failure("Activate remote repairers in space and outside warp with a valid repeat count.");
            }
            Start(context, module, effect, (long)args[2]!, (long)args[3]!);
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    public void Dispose()
    {
        signal.Applied -= Receive;
        _subscribed = false;
        foreach (var cycle in _cycles.Values)
        {
            repairing.Stop(cycle.PilotId, cycle.Repair.Module.ItemId);
        }
        _cycles.Clear();
        lock (_received)
        {
            _pilotId = 0;
            _received.Clear();
        }
    }
}
