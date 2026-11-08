using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Repair;

[Injectable(InjectionType.Scoped)]
internal sealed class NaniteRepairHandler(
    IPilotNaniteRepair repairs,
    TimeProvider clock,
    IEnumerable<IModuleEffectSource> effects,
    IPilotInventory? inventory = null
) : ISelectedPilotCallHandler
{
    // Build 3662168's ModuleButton.RepairModule catches this name while
    // continuing the remaining bank members after insufficient paste.
    private const string MissingPasteError = "NotEnoughRepairMaterialToFinishAllRepairs";
    private const int MaximumModules = 3 * InventoryDefinitions.SlotsPerRack;
    private readonly Dictionary<long, NaniteRepairSchedule> _active = [];
    private IReadOnlyList<NaniteRepairSchedule>? _view;
    private IReadOnlyDictionary<long, InventoryItem> _paste = new Dictionary<long, InventoryItem>();
    private long _pilot;
    private long _ship;
    private int _system;

    internal bool IsMonitoring => _active.Count > 0;
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    internal void Enter(PilotCallContext context)
    {
        Cancel(context);
        _pilot = context.Pilot.PilotId;
        _ship = context.Pilot.ShipId;
        _system = context.Pilot.SystemId;
        // A fresh native Godma instance has lost its local repair timestamps.
        // Settle any orphaned paid work before the incoming ship is re-primed.
        if (repairs.Read(_pilot, _ship, Now).Count > 0)
        {
            Notify(context, repairs.Cancel(_pilot, _ship, Now));
        }
        _active.Clear();
        _view = null;
    }

    internal void Cancel(PilotCallContext context)
    {
        if (_active.Count > 0)
        {
            var settled = repairs.Cancel(_pilot, _ship, Now);
            Notify(context, settled);
            var published = settled.Select(c => c.ItemId).ToHashSet();
            foreach (var schedule in _active.Values.ToArray())
            {
                if (!published.Contains(schedule.Key.ModuleId))
                {
                    // The world owner may already have settled docking, loss
                    // or expiry before this socket observes the transition.
                    Stop(context, schedule, notifyPaste: false);
                }
            }
            NotifyPaste(context);
        }
        _active.Clear();
        _view = null;
    }

    internal void Advance(PilotCallContext context)
    {
        if (_active.Count == 0)
        {
            return;
        }
        if (
            !context.Selected
            || !context.Flight.CanUseSelfModules
            || context.Pilot.PilotId != _pilot
            || context.Pilot.ShipId != _ship
            || context.Pilot.SystemId != _system
        )
        {
            Cancel(context);
            return;
        }
        var view = repairs.Read(_pilot, _ship, Now);
        if (ReferenceEquals(view, _view))
        {
            return;
        }
        _view = view;
        var current = view.Select(r => r.Key).ToHashSet();
        foreach (var schedule in _active.Values.ToArray())
        {
            if (!current.Contains(schedule.Key))
            {
                Stop(context, schedule);
            }
        }
    }

    private void Stop(PilotCallContext context, NaniteRepairSchedule schedule, bool notifyPaste = true)
    {
        try
        {
            if (repairs.Stop(_pilot, _ship, schedule.Key, Now) is { } condition)
            {
                Notify(context, [condition]);
            }
        }
        catch (InvalidDataException)
        {
            // Destruction or ownership transfer has already removed this
            // pilot's fitted item; the next location prime owns its display.
        }
        _active.Remove(schedule.Key.ModuleId);
        if (notifyPaste)
        {
            NotifyPaste(context);
        }
    }

    private IReadOnlyDictionary<long, InventoryItem> Paste() =>
        inventory
            ?.List(_pilot, _ship)
            .Where(i => i.TypeId == NaniteRepairRules.PasteType && i.FlagId == (int)InventoryFlag.Cargo)
            .ToDictionary(i => i.Id)
        ?? new Dictionary<long, InventoryItem>();

    private void NotifyPaste(PilotCallContext context)
    {
        var current = Paste();
        foreach (var item in current.Values)
        {
            var previous = _paste.GetValueOrDefault(item.Id);
            if (item != previous)
            {
                InventoryChangesWire.NotifyItemChange(context, new(item, previous));
            }
        }
        foreach (var item in _paste.Values.Where(i => !current.ContainsKey(i.Id)))
        {
            InventoryChangesWire.NotifyItemChange(context, new(item with { Quantity = 0 }, item));
        }
        _paste = current;
    }

    private void Notify(PilotCallContext context, IReadOnlyList<ModuleCondition> conditions)
    {
        if (conditions.Count > 0)
        {
            long now = Now;
            context.Notifications.Add(
                new ModuleAttributesChanged(
                    conditions
                        .Select(c => new ModuleAttributeChange(
                            _pilot,
                            new DogmaItemKey.Item(c.ItemId),
                            DogmaAttributeId.Damage,
                            now,
                            c.Damage
                        ))
                        .ToArray()
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
        if (service != "dogmaIM" || method is not ("InitiateModuleRepair" or "InitiateModuleRepairMany" or "StopModuleRepair"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (method == "StopModuleRepair" && args is [long stopping])
            {
                if (_active.TryGetValue(stopping, out var schedule))
                {
                    Stop(context, schedule);
                }
                return PilotCallResult.None;
            }
            bool many = method == "InitiateModuleRepairMany";
            long[] modules;
            if (!many && method == "InitiateModuleRepair" && args is [long module])
            {
                modules = [module];
            }
            else if (many && args is [List<object?> ids] && ids.All(i => i is long))
            {
                modules = ids.Cast<long>().ToArray();
            }
            else
            {
                return PilotCallResult.Unsupported;
            }
            if (!context.Flight.CanUseSelfModules)
            {
                return new PilotCallResult.Failure("Repair fitted modules on your ship in space outside a session transition.");
            }
            if (modules.Length is 0 or > MaximumModules || modules.Any(id => id <= 0) || modules.Distinct().Count() != modules.Length)
            {
                return new PilotCallResult.Failure("Select distinct fitted modules to repair.");
            }
            if (
                _active.Count > 0
                && (context.Pilot.PilotId != _pilot || context.Pilot.ShipId != _ship || context.Pilot.SystemId != _system)
            )
            {
                Cancel(context);
            }
            _pilot = context.Pilot.PilotId;
            _ship = context.Pilot.ShipId;
            _system = context.Pilot.SystemId;
            var busy = modules.Where(id => effects.Any(source => source.ActiveEffect(context.Pilot, id) is not null)).ToHashSet();
            var eligible = modules.Where(id => !busy.Contains(id)).ToArray();
            var result = eligible.Length == 0 ? new NaniteRepairBatch([], [], [], []) : repairs.Start(_pilot, _ship, eligible, Now);
            foreach (var change in result.Inventory)
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            _paste = Paste();
            foreach (var started in result.Started)
            {
                if (started.Schedule is { } schedule)
                {
                    _active[started.ModuleId] = schedule;
                }
                else
                {
                    Notify(context, [started.Condition]);
                }
            }
            _view = null;
            if (many)
            {
                var failed = result.Failed.Concat(busy).ToHashSet();
                return new NaniteRepairBatchReply(
                    result.Started.Select(r => r.ModuleId).ToArray(),
                    result.MissingPaste,
                    modules.Where(failed.Contains).ToArray()
                );
            }
            if (result.MissingPaste.Count > 0)
            {
                return new PilotCallResult.NamedFailure(MissingPasteError, BlueEmpty.Dictionary);
            }
            return result.Started.Count > 0;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The nanite repair exceeds the saved timing or quantity limit.");
        }
    }
}
