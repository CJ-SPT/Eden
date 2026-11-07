using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Characters;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Drones;

[Injectable(InjectionType.Scoped)]
internal sealed class DroneHandler(
    IPilotDrones drones,
    TimeProvider clock,
    TargetingHandler targeting,
    CharacterInteractionHistoryHandler? history = null
) : ISelectedPilotCallHandler
{
    private long _nextTick;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "dogmaIM" && method == "ChangeDroneSettings")
        {
            if (
                keywords is { Count: > 0 }
                || args is not [Dictionary<long, object?> settings]
                || settings.Count is < 1 or > 2
                || settings.Any(setting =>
                    setting.Key is not ((long)DogmaAttributeId.DroneIsAggressive) and not ((long)DogmaAttributeId.DroneFocusFire)
                    || setting.Value is not bool and not (long and (0 or 1)) and not (double and (0d or 1d))
                )
            )
            {
                return new PilotCallResult.Failure("Choose valid drone aggression and focus-fire preferences.");
            }
            bool? Setting(DogmaAttributeId id) => settings.TryGetValue((long)id, out var value) ? value is true or 1L or 1d : null;
            drones.ChangeSettings(
                context.Pilot.PilotId,
                Setting(DogmaAttributeId.DroneIsAggressive),
                Setting(DogmaAttributeId.DroneFocusFire)
            );
            DroneWire.NotifyControl(context, drones.Control(context.Pilot.PilotId), clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            return PilotCallResult.None;
        }
        bool launch = service == "ship" && method == "LaunchDrones";
        bool scoop = service == "ship" && method is "Scoop" or "ScoopDrone";
        bool command =
            service == "entity"
            && method is "CmdEngage" or "CmdReturnHome" or "CmdReturnBay" or "CmdReconnectToDrones" or "CmdAbandonDrone";
        if (!launch && !command && !scoop)
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 } || !context.Flight.CanTarget)
        {
            return new PilotCallResult.Failure("Use drones while your ship is present in space.");
        }
        try
        {
            context.Flight.Checkpoint();
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            if (scoop)
            {
                bool toBay = method == "ScoopDrone";
                IReadOnlyList<long> selection = (toBay, args) switch
                {
                    (true, [var items]) => ReadIds(items),
                    (false, [long item]) => [item],
                    _ => throw new InvalidDataException("Choose a drone to scoop."),
                };
                var hold = toBay ? InventoryFlag.DroneBay : InventoryFlag.Cargo;
                Notify(context, drones.Scoop(context.Pilot.PilotId, selection, hold, now));
                context.Flight.RefreshDrones();
                return toBay ? new DroneCommandReply() : PilotCallResult.None;
            }
            if (launch)
            {
                if (args is not [List<object?> items, var behalf, bool] || behalf is not null && !Equals(behalf, context.Pilot.PilotId))
                {
                    return new PilotCallResult.Failure("Choose drones from your own ship's drone bay.");
                }
                var requests = items
                    .Select(item =>
                        item is object?[] tuple && tuple is [long id, long quantity] && quantity is > 0 and <= int.MaxValue
                            ? new DroneStack(id, (int)quantity)
                            : throw new InvalidDataException("Invalid drone launch selection.")
                    )
                    .ToArray();
                var result = drones.Launch(context.Pilot.PilotId, requests, now);
                Notify(context, result.Changes);
                context.Flight.RefreshDrones();
                DroneWire.NotifyControl(context, drones.Control(context.Pilot.PilotId), now);
                return new DroneLaunchReply(result.Launches);
            }
            DroneOrder order = method switch
            {
                "CmdEngage" => DroneOrder.Engage,
                "CmdReturnBay" => DroneOrder.ReturnToBay,
                "CmdReconnectToDrones" => DroneOrder.Reconnect,
                "CmdAbandonDrone" => DroneOrder.Abandon,
                _ => DroneOrder.Orbit,
            };
            long? target = null;
            IReadOnlyList<long> ids;
            if (method == "CmdReconnectToDrones" && args.Length == 0)
            {
                context.Flight.RefreshDrones();
                ids = context
                    .Flight.VisibleDrones.Where(d => d.PilotId == context.Pilot.PilotId && d.ShipId == 0)
                    .Select(d => d.Id)
                    .Take(DroneRules.MaximumDrones)
                    .ToArray();
            }
            else
            {
                if (args.Length != (order == DroneOrder.Engage ? 2 : 1))
                {
                    throw new InvalidDataException("Invalid drone command arguments.");
                }
                ids = ReadIds(args[0]);
                if (order == DroneOrder.Engage)
                {
                    target = args[1] as long? ?? throw new InvalidDataException("Choose a locked NPC target.");
                }
            }
            targeting.Advance(context);
            var locks = context.Flight.VisibleNpcs.Where(n => targeting.IsLocked(n.Id)).Select(n => n.Id).ToHashSet();
            drones.Order(context.Pilot.PilotId, ids, order, target, locks, now);
            context.Flight.RefreshDrones();
            DroneWire.NotifyControl(context, drones.Control(context.Pilot.PilotId), now);
            return new DroneCommandReply();
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static IReadOnlyList<long> ReadIds(object? value) =>
        value switch
        {
            // The overview passes a Python tuple. The drone panel passes a list.
            object?[] items when items.All(item => item is long) => items.Cast<long>().ToArray(),
            List<object?> items when items.All(item => item is long) => items.Cast<long>().ToArray(),
            HashSet<long> items => items.ToArray(),
            _ => throw new InvalidDataException("Choose a list of drone identities."),
        };

    private static void Notify(PilotCallContext context, IReadOnlyList<InventoryChange> changes)
    {
        foreach (var change in changes)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected || !context.Flight.CanTarget)
        {
            return;
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (now < _nextTick)
        {
            return;
        }
        _nextTick = checked(now + TimeSpan.TicksPerSecond);
        if (!drones.HasControlled(context.Pilot.PilotId, context.Pilot.ShipId))
        {
            context.Flight.RefreshDrones();
            return;
        }
        var result = drones.Advance(context.Pilot.PilotId, now, context.Flight.CurrentStamp);
        Notify(context, result.Changes);
        if (result.Changes.Count > 0 || result.ControlChanged)
        {
            DroneWire.NotifyControl(context, drones.Control(context.Pilot.PilotId), clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        }
        foreach (var hit in result.Hits)
        {
            context.NotificationsBeforeFlight.Add(
                new DamageMessageNotification(
                    new OutgoingCombatMessage(hit.Applied, CombatMessages.HitQuality(hit.Quality), hit.TargetId, hit.TypeId, hit.Damage)
                )
            );
            context.Flight.NotifyHealth(hit.Health);
            if (hit.Health.Remaining.Destroyed)
            {
                history?.NotifyDestruction(context);
            }
        }
        context.Flight.RefreshDrones();
        if (result.Hits.Count > 0)
        {
            context.Flight.RefreshNpcs();
            context.Flight.RefreshWrecks();
        }
    }
}
