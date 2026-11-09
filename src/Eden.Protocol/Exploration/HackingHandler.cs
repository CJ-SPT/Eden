using System.Globalization;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Exploration;

[Injectable(InjectionType.Scoped)]
internal sealed class HackingHandler(IPilotHacking hacking, TargetingHandler targeting, SupportModuleWire attributes, TimeProvider clock)
    : ISelectedPilotCallHandler,
        IModuleEffectSource
{
    private const string ContainerPrefix = "@explorationcontainer:";
    internal const string EffectName = "doHacking";
    private HackingContact? _active;
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
    internal bool IsMonitoring => _active is not null;

    private static DogmaActiveEffect Effect(HackingContact contact) =>
        new(
            contact.Module.Id,
            contact.PilotId,
            contact.ShipId,
            contact.ContainerId,
            DogmaEffect.Hacking,
            contact.StartedAt,
            contact.Module.DurationMs,
            0
        );

    private static SupportContact SpaceEffect(HackingContact contact) =>
        new(
            new(contact.Module.Id, contact.Module.TypeId, DogmaEffect.Hacking, contact.Module.DurationMs, contact.Module.CapacitorNeed),
            contact.StartedAt,
            contact.ContainerId
        );

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _active is { } active && active.ShipId == pilot.ShipId && active.Module.Id == module ? Effect(active) : null;

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_active?.Module.Id == module)
        {
            Cancel(context);
        }
    }

    internal void Cancel(PilotCallContext context)
    {
        if (_active is not { } active)
        {
            return;
        }
        hacking.Cancel(active.PilotId, Now);
        context.Notifications.Add(new HackingTurnCompleted(active, active.Board with { Outcome = HackingOutcome.Lost }, active.Board));
        Stop(context);
    }

    private void Stop(PilotCallContext context)
    {
        if (_active is not { } active)
        {
            return;
        }
        context.Notifications.Add(new ShipEffectChanged(Effect(active), Now, false, false, Now));
        if (context.Pilot.ShipId == active.ShipId)
        {
            context.Flight.SetSupportEffect(SpaceEffect(active), false);
        }
        _active = null;
    }

    internal void Advance(PilotCallContext context)
    {
        if (_active is not { } active)
        {
            return;
        }
        var target = context.Flight.FindTarget(active.ContainerId);
        if (
            context.Pilot.PilotId != active.PilotId
            || context.Pilot.ShipId != active.ShipId
            || context.Pilot.SystemId != active.SystemId
            || !context.Flight.CanTarget
            || context.Flight.PreparingWarp
            || !targeting.IsLocked(active.ContainerId)
            || target is null
            || context.Pilot.Position!.DistanceTo(target.Position) - target.Radius - context.Pilot.Flight.ShipRadius > active.Module.Range
        )
        {
            Cancel(context);
        }
    }

    internal PilotCallResult CallModule(PilotCallContext context, string method, object?[] args)
    {
        try
        {
            context.Flight.Checkpoint();
            targeting.Advance(context);
            Advance(context);
            if (method == "Deactivate" && args is [long module, _])
            {
                CancelModule(context, module);
                return PilotCallResult.None;
            }
            if (method != "Activate" || args is not [long id, _, long target, long repeats] || repeats < 0 || repeats > int.MaxValue)
            {
                return new PilotCallResult.Failure("Invalid analyzer activation arguments.");
            }
            if (!context.Flight.CanTarget || context.Flight.PreparingWarp)
            {
                throw new InvalidDataException("Activate the analyzer in space and outside warp.");
            }
            if (_active is { } active)
            {
                return active.Module.Id == id && active.ContainerId == target
                    ? PilotCallResult.None
                    : new PilotCallResult.Failure("Finish or quit the current hacking attempt first.");
            }
            _active = hacking.Begin(context.Pilot.PilotId, id, target, targeting.IsLocked(target) ? new HashSet<long> { target } : [], Now);
            context.Notifications.Add(new ShipEffectChanged(Effect(_active), Now, true, false));
            context.Flight.SetSupportEffect(SpaceEffect(_active), true);
            attributes.Refresh(context, _active.Capacitor);
            context.Notifications.Add(new HackingTurnCompleted(_active, _active.Board));
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static HackingCoordinate Coordinate(object? value) =>
        value is object?[] point && point is [long x, long y] && x is >= 0 and < HackingRules.Width && y is >= 0 and < HackingRules.Height
            ? new((int)x, (int)y)
            : throw new InvalidDataException("Invalid hacking tile coordinate.");

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool bind = service == "invbroker" && method == "GetInventoryFromId" && args is [long, ..];
        bool container = service.StartsWith(ContainerPrefix, StringComparison.Ordinal);
        if (service != "hackingMgr" && !bind && !container)
        {
            return PilotCallResult.Unsupported;
        }
        long id = bind ? (long)args[0]! : 0;
        if (container && !long.TryParse(service.AsSpan(ContainerPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id))
        {
            return new PilotCallResult.Failure("Invalid exploration container reference.");
        }
        if ((bind || container) && !HackingRules.IsContainer(id))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            context.Flight.Checkpoint();
            if (bind || container)
            {
                if (!context.Flight.CanTarget)
                {
                    throw new InvalidDataException("Open exploration containers in space and outside warp.");
                }
                var contents = hacking.Read(context.Pilot.PilotId, id, Now);
                if (bind)
                {
                    return context.Reference(ContainerPrefix + id.ToString(CultureInfo.InvariantCulture), PilotBindingLifetime.Location);
                }
                if (method == "GetSelfInvItem" && args.Length == 0)
                {
                    return InventoryRow.From(contents.Container);
                }
                if (method == "List")
                {
                    object? flag = args.FirstOrDefault() ?? keywords?.GetValueOrDefault("flag");
                    return new BlueList<InventoryRow>(
                        contents.Items.Where(i => flag is null || Equals(flag, (long)i.FlagId)).Select(InventoryRow.From).ToArray()
                    );
                }
                return PilotCallResult.Unsupported;
            }
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected hacking arguments.");
            }
            targeting.Advance(context);
            Advance(context);
            if (method == "StartNewGameInstance")
            {
                return new PilotCallResult.Failure("Activate a fitted analyzer on an exploration container to begin hacking.");
            }
            if (method == "QuitHackingAttempt" && args.Length == 0)
            {
                Cancel(context);
                return PilotCallResult.None;
            }
            HackingAction action = (method, args) switch
            {
                ("ClickedOnTile", [var coordinate]) => new(HackingActionKind.Click, Coordinate(coordinate)),
                ("UsedUtilityElement", [long slot, var coordinate]) when slot is >= 0 and < HackingRules.MaximumSlots => new(
                    HackingActionKind.UtilityOnTile,
                    Coordinate(coordinate),
                    (int)slot
                ),
                ("UsedUtilityElementOnVirus", [long slot]) when slot is >= 0 and < HackingRules.MaximumSlots => new(
                    HackingActionKind.UtilityOnVirus,
                    Slot: (int)slot
                ),
                _ => throw new InvalidDataException("Invalid hacking command."),
            };
            if (_active is null)
            {
                throw new InvalidDataException("No hacking attempt is active.");
            }
            var turn = hacking.Act(context.Pilot.PilotId, action, Now);
            context.Notifications.Add(new HackingTurnCompleted(turn.Contact, turn.Current, turn.Previous, turn.Action));
            _active = turn.Contact with { Board = turn.Current };
            if (turn.Current.Outcome != HackingOutcome.Running)
            {
                Stop(context);
            }
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            if (service == "hackingMgr" && _active is { } active)
            {
                context.Notifications.Add(new HackingTurnCompleted(active, active.Board, active.Board));
            }
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
