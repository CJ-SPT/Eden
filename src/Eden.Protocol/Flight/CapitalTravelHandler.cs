using System.Collections.Concurrent;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Flight;

[Injectable(InjectionType.Scoped)]
internal sealed partial class CapitalTravelHandler(
    IPilotCapitalTravel travel,
    ISystemObjects systems,
    TimeProvider clock,
    CapitalInventorySignal fuelSignal,
    IPilotFitting? fitting = null,
    IPilotShipCompression? compression = null,
    Eden.Protocol.Targeting.TargetingHandler? targeting = null,
    PropulsionHandler? propulsion = null
) : ISelectedPilotCallHandler, IModuleEffectSource, IDisposable
{
    internal const string CynoEffectName = "cynosuralGeneration";
    internal const string TitanPortalEffectName = "jumpPortalGeneration";
    internal const string PortalEffectName = "jumpPortalGenerationBO";
    private DogmaActiveEffect? _lastEffect;
    private int _lastModeTypeId;
    private readonly ConcurrentQueue<(long Pilot, InventoryChange Change)> _fuelChanges = new();
    private long _recipient;
    private bool _subscribed;

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
            case ("inSpaceCompressionMgr", "CompressItemInSpace"):
                return Compress(context, args, plain);
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
            case ("beyonce", "CmdJumpToFleetModuleBeacon" or "CmdBridgeToFleetModuleBeacon" or "CmdGroupJumpToFleetModuleBeacon"):
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
                var moduleTarget = new CapitalJumpRequest(
                    emitter,
                    beacon,
                    (int)system,
                    Conduit: method == "CmdGroupJumpToFleetModuleBeacon"
                );
                return method == "CmdBridgeToFleetModuleBeacon" ? OpenPortal(context, moduleTarget) : StartJump(context, moduleTarget);
            case (
                "beyonce",
                "CmdJumpToFleetDeployableBeacon"
                    or "CmdBridgeToFleetDeployableBeacon"
                    or "CmdGroupJumpToFleetDeployableBeacon"
            ):
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
                var mobileTarget = new CapitalJumpRequest(
                    deployable,
                    deployedBeacon,
                    (int)deployableSystem,
                    CapitalJumpTarget.FleetDeployable,
                    Conduit: method == "CmdGroupJumpToFleetDeployableBeacon"
                );
                return method == "CmdBridgeToFleetDeployableBeacon" ? OpenPortal(context, mobileTarget) : StartJump(context, mobileTarget);
            case ("beyonce", "CmdJumpThroughFleet"):
                if (
                    !plain
                    || args is not [long owner, long host, long portalBeacon, long portalSystem]
                    || owner <= 0
                    || host <= 0
                    || portalBeacon <= 0
                    || portalSystem is <= 0 or > int.MaxValue
                )
                {
                    return new PilotCallResult.Failure("Choose a valid fleet jump portal.");
                }
                return StartJump(context, new(owner, portalBeacon, (int)portalSystem, CapitalJumpTarget.FleetPortal, host));
            case ("structureCynoBeaconMgr", "CmdJumpToStructureBeacon" or "CmdBridgeToStructureBeacon" or "CmdGroupJumpToStructureBeacon"):
                if (!plain || args is not [long structure, long destination] || structure <= 0 || destination is <= 0 or > int.MaxValue)
                {
                    return new PilotCallResult.Failure("Choose a valid structure cynosural beacon.");
                }
                var structureTarget = new CapitalJumpRequest(
                    0,
                    structure,
                    (int)destination,
                    CapitalJumpTarget.Structure,
                    Conduit: method == "CmdGroupJumpToStructureBeacon"
                );
                return method == "CmdBridgeToStructureBeacon" ? OpenPortal(context, structureTarget) : StartJump(context, structureTarget);
            default:
                return PilotCallResult.Unsupported;
        }
    }

    internal bool AdoptConduit(PilotCallContext context)
    {
        if (!context.Selected || context.Flight.TakeConduitArrival() is not { } moved)
        {
            return false;
        }
        context.Flight.Relocate(moved.Pilot, systems.Load(moved.Pilot.SystemId));
        context.ReleaseLocationReferences();
        context.Roster = context.Roster.Select(pilot => pilot.PilotId == moved.Pilot.PilotId ? moved.Pilot : pilot).ToArray();
        foreach (var change in moved.Changes)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
        context.Notifications.Add(new JumpTimersUpdated(moved.Timers));
        return true;
    }

    private PilotCallResult OpenPortal(PilotCallContext context, CapitalJumpRequest request)
    {
        var flight = context.Flight;
        if (!flight.CanUseSelfModules || flight.InWarp || flight.PreparingWarp || flight.Cloaked || flight.MicroJumpSpooling)
        {
            return new PilotCallResult.Failure("Wait until your ship is ready to open a jump portal.");
        }
        try
        {
            flight.Checkpoint();
            var pilot = context.Pilot;
            var opened = travel.OpenPortal(pilot.PilotId, pilot.ShipId, request);
            flight.StopForCapitalModule();
            foreach (var change in opened.FuelChanges)
            {
                InventoryChangesWire.NotifyItemChange(context, change, beforeFlight: true);
            }
            context.NotificationsBeforeFlight.Add(
                new ModuleAttributesChanged([
                    new(pilot.PilotId, new DogmaItemKey.Item(pilot.ShipId), DogmaAttributeId.CapacitorCharge, Now, opened.Capacitor),
                ])
            );
            Advance(context);
            return true;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
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
        if (BlueMarshal.Text(args[1]) is PortalEffectName or TitanPortalEffectName)
        {
            return new PilotCallResult.Failure(
                method == "Deactivate"
                    ? "A jump portal closes at the end of its paid cycle."
                    : "Choose a destination beacon from the fleet bridge menu."
            );
        }
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
            flight.StopForCapitalModule();
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
        long pilot = context.Selected ? context.Pilot.PilotId : 0;
        Volatile.Write(ref _recipient, pilot);
        if (pilot != 0 && !_subscribed)
        {
            fuelSignal.FuelSpent += EnqueueFuel;
            _subscribed = true;
        }
        while (_fuelChanges.TryDequeue(out var spent))
        {
            if (spent.Pilot == pilot)
            {
                InventoryChangesWire.NotifyItemChange(context, spent.Change, beforeFlight: true);
            }
        }
        AdvanceCompressors(context);
        AdvanceProtection(context);
        var mode = pilot != 0 && CapitalModeRules.CanUseMode(context.Pilot.ShipGroupId) ? travel.Mode(context.Pilot.ShipId) : null;
        var repeat = RepeatBastion(context, mode);
        mode = repeat ?? mode;
        var current =
            pilot == 0 ? null
            : travel.Field(pilot) is { } field ? Effect(field)
            : travel.Portal(pilot) is { } portal ? Effect(portal)
            : mode is not null ? Effect(mode)
            : null;
        if (_lastEffect == current)
        {
            return;
        }
        if (_lastEffect is { } ended && context.Selected && ended.PilotId == context.Pilot.PilotId)
        {
            if (repeat is null)
            {
                context.NotificationsBeforeFlight.Add(new ShipEffectChanged(ended, Now, false, false, Now));
            }
            CapitalVisual(context.Flight, ended, false, _lastModeTypeId);
            if (
                repeat is null
                && ended.ShipId == context.Pilot.ShipId
                && ended.Effect
                    is DogmaEffect.CapitalSiege
                        or DogmaEffect.CapitalTriage
                        or DogmaEffect.Bastion
                        or DogmaEffect.CapitalIndustrialCore
                        or DogmaEffect.CloneJumpAccepting
                && fitting is not null
            )
            {
                RefreshModeFitting(context, fitting.ShipAttributes(pilot), ended.Effect);
            }
        }
        _lastEffect = current;
        _lastModeTypeId = mode?.TypeId ?? 0;
        if (current is { } active)
        {
            // The native ramp continues uninterrupted when the next paid cycle begins.
            if (repeat is null)
            {
                context.NotificationsBeforeFlight.Add(new ShipEffectChanged(active, Now, true, true));
            }
            CapitalVisual(context.Flight, active, true, _lastModeTypeId);
        }
    }

    private static void CapitalVisual(FlightSession flight, DogmaActiveEffect effect, bool active, int modeTypeId)
    {
        var graphic = effect.Effect switch
        {
            DogmaEffect.TitanJumpPortalGeneration => "effects.JumpPortal",
            DogmaEffect.CovertJumpPortalGeneration => "effects.JumpPortalBO",
            DogmaEffect.CapitalSiege or DogmaEffect.Bastion or DogmaEffect.CapitalIndustrialCore => "effects.SiegeMode",
            DogmaEffect.CapitalTriage => "effects.TriageMode",
            _ => null,
        };
        if (graphic is not null && flight.Pilot.ShipId == effect.ShipId)
        {
            flight.SetWeaponEffect(
                new(
                    effect.ModuleId,
                    effect.Effect switch
                    {
                        DogmaEffect.TitanJumpPortalGeneration => flight.Pilot.ShipGroupId == (int)HullClass.CapitalIndustrial
                            ? CapitalTravelRules.IndustrialPortalGenerator
                            : CapitalTravelRules.TitanPortalGenerator,
                        DogmaEffect.CovertJumpPortalGeneration => CapitalTravelRules.CovertPortalGenerator,
                        DogmaEffect.Bastion => modeTypeId,
                        _ => 0,
                    },
                    0,
                    null,
                    effect.StartTime,
                    effect.DurationMilliseconds ?? 0,
                    graphic,
                    Offensive: effect.Effect == DogmaEffect.Bastion
                ),
                active
            );
        }
    }

    private void EnqueueFuel(long pilot, IReadOnlyList<InventoryChange> changes)
    {
        if (pilot == Volatile.Read(ref _recipient))
        {
            foreach (var change in changes)
            {
                _fuelChanges.Enqueue((pilot, change));
            }
        }
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            fuelSignal.FuelSpent -= EnqueueFuel;
        }
    }

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _lastEffect is { } effect && effect.PilotId == pilot.PilotId && effect.ShipId == pilot.ShipId && effect.ModuleId == module ? effect
        : _compressors.FirstOrDefault(cycle => cycle.PilotId == pilot.PilotId && cycle.ShipId == pilot.ShipId && cycle.ModuleId == module)
            is { } compressor
            ? Effect(compressor)
        : null;

    public void CancelModule(PilotCallContext context, long module)
    {
        if (context.Selected)
        {
            CancelBastionRepeat(context.Pilot, module);
        }
        // Resident paid effects survive connection cleanup and end at their deadlines.
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

    private static DogmaActiveEffect Effect(JumpPortal portal) =>
        new(
            portal.ModuleId,
            portal.PilotId,
            portal.ShipId,
            null,
            portal.ModuleTypeId == CapitalTravelRules.CovertPortalGenerator
                ? DogmaEffect.CovertJumpPortalGeneration
                : DogmaEffect.TitanJumpPortalGeneration,
            portal.StartedAt,
            (portal.EndsAt - portal.StartedAt) / (double)TimeSpan.TicksPerMillisecond,
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
