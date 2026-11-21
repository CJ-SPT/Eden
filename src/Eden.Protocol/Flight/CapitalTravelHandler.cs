using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Flight;

[Injectable(InjectionType.Scoped)]
internal sealed class CapitalTravelHandler(IPilotCapitalTravel travel, ISystemObjects systems, TimeProvider clock)
    : ISelectedPilotCallHandler,
        IModuleEffectSource
{
    internal const string CynoEffectName = "cynosuralGeneration";
    private DogmaActiveEffect? _lastEffect;

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        var pilot = context.Pilot;
        bool plain = keywords is null or { Count: 0 };
        switch (service, method)
        {
            case ("ship", "Drop"):
                return Drop(context, args, plain);
            case ("jumpTimers", "GetTimers"):
                return plain && args is [long id] && id == pilot.PilotId
                    ? new PilotCallResult.Success(new JumpTimerState(travel.Timers(id)))
                    : new PilotCallResult.Failure("Read jump timers for your selected pilot.");
            case ("map", "GetBeaconCount"):
                return plain && args.Length == 0
                    ? new PilotCallResult.Success(new BlueMap<int, int>(travel.BeaconCounts()))
                    : new PilotCallResult.Failure("Cynosural beacon counts do not accept arguments.");
            case ("beyonce", "CmdJumpToFleetModuleBeacon"):
                if (
                    !plain
                    || args is not [long emitter, long beacon, long system]
                    || emitter <= 0
                    || beacon <= 0
                    || system is <= 0 or > int.MaxValue
                )
                {
                    return new PilotCallResult.Failure("Choose a valid fleet cynosural field.");
                }
                return StartJump(context, new(emitter, beacon, (int)system));
            case ("beyonce", "CmdJumpToFleetDeployableBeacon"):
                if (
                    !plain
                    || args is not [long deployable, long deployableSystem, long deployedBeacon]
                    || deployable <= 0
                    || deployedBeacon <= 0
                    || deployableSystem is <= 0 or > int.MaxValue
                )
                {
                    return new PilotCallResult.Failure("Choose a valid fleet deployable beacon.");
                }
                return StartJump(context, new(deployable, deployedBeacon, (int)deployableSystem, CapitalJumpTarget.FleetDeployable));
            case ("structureCynoBeaconMgr", "CmdJumpToStructureBeacon"):
                if (!plain || args is not [long structure, long destination] || structure <= 0 || destination is <= 0 or > int.MaxValue)
                {
                    return new PilotCallResult.Failure("Choose a valid structure cynosural beacon.");
                }
                return StartJump(context, new(0, structure, (int)destination, CapitalJumpTarget.Structure));
            default:
                return PilotCallResult.Unsupported;
        }
    }

    private PilotCallResult Drop(PilotCallContext context, object?[] args, bool plain)
    {
        var pilot = context.Pilot;
        if (!plain || args is not [List<object?> items, var owner, bool _] || owner is not (null or 0L) && !Equals(owner, pilot.PilotId))
        {
            return new PilotCallResult.Failure("Choose mobile deployables to launch for your pilot.");
        }
        long[] ids = items
            .Select(value =>
                value switch
                {
                    long id => id,
                    object?[] and [long id, 1L] => id,
                    _ => 0,
                }
            )
            .ToArray();
        if (ids.Any(id => id <= 0))
        {
            return new PilotCallResult.Failure("Choose one unit from each cargo stack.");
        }
        var flight = context.Flight;
        if (!flight.CanUseSelfModules || flight.InWarp || flight.PreparingWarp || flight.Cloaked || flight.MicroJumpSpooling)
        {
            return new PilotCallResult.Failure("Launch a deployable from your active ship in space outside warp.");
        }
        try
        {
            flight.Checkpoint();
            var deployment = travel.Deploy(pilot.PilotId, pilot.ShipId, ids, flight.Heading);
            foreach (var change in deployment.Changes)
            {
                InventoryChangesWire.NotifyItemChange(context, change, beforeFlight: true);
            }
            flight.RefreshCynos();
            return new BlueMap<long, BlueList<long>>(
                deployment.Launched.ToDictionary(row => row.Key, row => new BlueList<long>([row.Value]))
            );
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private PilotCallResult StartJump(PilotCallContext context, CapitalJumpRequest request)
    {
        try
        {
            context.Flight.Checkpoint();
            context.Flight.StartCapitalJump(travel, request, systems);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    internal PilotCallResult Activate(PilotCallContext context, string method, object?[] args)
    {
        var pilot = context.Pilot;
        if (method == "Deactivate" && args is [long module, _] && travel.Field(pilot.PilotId)?.ModuleId == module)
        {
            return new PilotCallResult.Failure("An active cynosural field must complete its paid cycle.");
        }
        if (
            method != "Activate"
            || args is not [long moduleId, _, var target, long repeats]
            || target is not (null or 0L) && !Equals(target, pilot.ShipId)
            || repeats is < 0 or > int.MaxValue
        )
        {
            return new PilotCallResult.Failure("Choose a cynosural field generator on your own ship.");
        }
        var flight = context.Flight;
        flight.Checkpoint();
        if (!flight.CanUseSelfModules || flight.Cloaked || flight.InWarp || flight.PreparingWarp || flight.MicroJumpSpooling)
        {
            return new PilotCallResult.Failure("Wait until your ship can light a cynosural field.");
        }
        try
        {
            var activation = travel.Light(pilot.PilotId, pilot.ShipId, moduleId, flight.Velocity.Length);
            flight.StopForCyno();
            flight.RefreshCynos();
            foreach (var change in activation.FuelChanges)
            {
                InventoryChangesWire.NotifyItemChange(context, change, beforeFlight: true);
            }
            context.NotificationsBeforeFlight.Add(
                new ModuleAttributesChanged([
                    new(pilot.PilotId, new DogmaItemKey.Item(pilot.ShipId), DogmaAttributeId.CapacitorCharge, Now, activation.Capacitor),
                ])
            );
            Advance(context);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    internal void Advance(PilotCallContext context)
    {
        var current = context.Selected && travel.Field(context.Pilot.PilotId) is { } field ? Effect(field) : null;
        if (_lastEffect == current)
        {
            return;
        }
        if (_lastEffect is { } ended && context.Selected && ended.PilotId == context.Pilot.PilotId)
        {
            context.NotificationsBeforeFlight.Add(new ShipEffectChanged(ended, Now, false, false, Now));
        }
        _lastEffect = current;
        if (current is { } active)
        {
            context.NotificationsBeforeFlight.Add(new ShipEffectChanged(active, Now, true, true));
        }
    }

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _lastEffect is { } effect && effect.PilotId == pilot.PilotId && effect.ShipId == pilot.ShipId && effect.ModuleId == module
            ? effect
            : null;

    public void CancelModule(PilotCallContext context, long module)
    {
        // A paid cyno is tied to its resident deadline and survives connection cleanup.
    }

    private static DogmaActiveEffect Effect(CynosuralField field) =>
        new(
            field.ModuleId,
            field.PilotId,
            field.ShipId,
            null,
            DogmaEffect.CynosuralGeneration,
            field.StartedAt,
            (field.EndsAt - field.StartedAt) / (double)TimeSpan.TicksPerMillisecond,
            0
        );
}

internal sealed record JumpTimerState(JumpTimers? Timers = null) : BlueModel
{
    public override object ToBlue() => Arguments(Timers);

    internal static object?[] Arguments(JumpTimers? timers) => [timers?.ActivationUntil, timers?.FatigueUntil, timers?.UpdatedAt];
}

internal sealed record CynoJammerDeadline(long OnlineAt) : BlueModel
{
    public override object ToBlue() => OnlineAt;
}

internal sealed record JumpTimersUpdated(JumpTimers Timers) : PilotNotification("OnJumpTimersUpdated")
{
    public override object?[] Arguments => JumpTimerState.Arguments(Timers);
}

internal sealed record CapitalJumpCancelled(string Message) : PilotNotification("OnRemoteMessage")
{
    public override object?[] Arguments => ["CustomNotify", new Dictionary<string, object?> { ["notify"] = Message }];
}
