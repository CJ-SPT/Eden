using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Fleets;

[Injectable(InjectionType.Scoped)]
internal sealed class CommandBurstHandler(
    TimeProvider clock,
    IPilotCommandBursts bursts,
    IPilotShipResources resources,
    IPilotFitting fitting,
    IPilotInventory inventory
) : IModuleEffectSource
{
    private sealed record Cycle(DogmaActiveEffect Effect, long ReadyAt, bool Stopping = false);

    private readonly Dictionary<long, Cycle> _cycles = [];
    private long _ship;
    private IReadOnlyList<CommandBurstBoost> _incoming = [];

    internal static DogmaEffect? Effect(string name) =>
        name switch
        {
            "moduleBonusWarfareLinkArmor" => DogmaEffect.ArmorCommandBurst,
            "moduleBonusWarfareLinkShield" => DogmaEffect.ShieldCommandBurst,
            "moduleBonusWarfareLinkSkirmish" => DogmaEffect.SkirmishCommandBurst,
            "moduleBonusWarfareLinkInfo" => DogmaEffect.InformationCommandBurst,
            "moduleBonusWarfareLinkMining" => DogmaEffect.MiningForemanBurst,
            _ => null,
        };

    private static bool CanPulse(FlightSession flight) => flight.CanUseSelfModules && !flight.Cloaked;

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.Effect.ShipId == pilot.ShipId ? cycle.Effect : null;

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_cycles.Remove(module, out var cycle))
        {
            long now = Now;
            context.Notifications.Add(new ShipEffectChanged(cycle.Effect, now, false, false, now));
        }
    }

    internal void Cancel(PilotCallContext context)
    {
        foreach (long module in _cycles.Keys.ToArray())
        {
            CancelModule(context, module);
        }
        _ship = 0;
        _incoming = [];
    }

    internal PilotCallResult Call(PilotCallContext context, string method, DogmaEffect effect, object?[] args)
    {
        if (method == "Deactivate" && args is [long stopping, _])
        {
            if (_cycles.TryGetValue(stopping, out var cycle) && cycle.Effect.Effect == effect)
            {
                _cycles[stopping] = cycle with { Stopping = true };
            }
            return PilotCallResult.None;
        }
        if (method != "Activate" || args is not [long module, _, var target, long repeats])
        {
            return PilotCallResult.Unsupported;
        }
        if (!CanPulse(context.Flight) || target is not null && !Equals(target, context.Pilot.ShipId) || repeats is < 0 or > int.MaxValue)
        {
            return new PilotCallResult.Failure("Activate a command burst without a target from an uncloaked ship in space.");
        }
        if (_cycles.ContainsKey(module))
        {
            return PilotCallResult.None;
        }
        try
        {
            Pulse(context, module, effect, repeats, Now, false);
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected || context.ActiveFlight is null)
        {
            return;
        }
        long now = Now;
        foreach (var (module, cycle) in _cycles.ToArray())
        {
            if (cycle.Effect.ShipId != context.Pilot.ShipId || !CanPulse(context.Flight))
            {
                CancelModule(context, module);
            }
            else if (now >= cycle.ReadyAt)
            {
                if (!cycle.Stopping && cycle.Effect.Repeats > 0)
                {
                    try
                    {
                        Pulse(context, module, cycle.Effect.Effect, cycle.Effect.Repeats - 1, now, true);
                        continue;
                    }
                    catch (Exception error) when (error is InvalidDataException or OverflowException) { }
                }
                CancelModule(context, module);
            }
        }
        if (context.Flight.InBallpark)
        {
            Refresh(context, now);
        }
    }

    private void Refresh(PilotCallContext context, long now)
    {
        long ship = context.Pilot.ShipId;
        bool hadBoosts = _ship == ship && _incoming.Count > 0;
        var incoming = bursts.Incoming(ship, now);
        if (_ship == ship && _incoming.SequenceEqual(incoming))
        {
            return;
        }
        _ship = ship;
        _incoming = incoming;
        if (hadBoosts || incoming.Count > 0)
        {
            context.Flight.NotifyCommandBursts(incoming);
            RefreshAttributes(context, now);
        }
    }

    private void RefreshAttributes(PilotCallContext context, long now)
    {
        long pilot = context.Pilot.PilotId;
        long ship = context.Pilot.ShipId;
        var snapshot = resources.Read(pilot, ship, now);
        context.Flight.RefreshFitting(snapshot.Attributes);
        context.Flight.NotifyHealth(snapshot.Health);
        List<ModuleAttributeChange> changes = [];
        Add(ship, snapshot.Attributes);
        changes.Add(new(pilot, new DogmaItemKey.Item(ship), DogmaAttributeId.ShieldCharge, now, snapshot.Health.Remaining.Shield));
        changes.Add(
            new(
                pilot,
                new DogmaItemKey.Item(ship),
                DogmaAttributeId.ArmorDamage,
                now,
                snapshot.Health.Defense.Armor - snapshot.Health.Remaining.Armor
            )
        );
        foreach (var module in inventory.List(pilot, ship).Where(item => InventoryDefinitions.IsFittedSlot(item.FlagId)))
        {
            Add(module.Id, fitting.ModuleAttributes(pilot, module));
        }
        context.Notifications.Add(new ModuleAttributesChanged(changes));

        void Add(long item, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
        {
            changes.AddRange(
                attributes.Select(pair => new ModuleAttributeChange(pilot, new DogmaItemKey.Item(item), pair.Key, now, pair.Value))
            );
        }
    }

    private void Pulse(PilotCallContext context, long module, DogmaEffect effect, long repeats, long now, bool repeating)
    {
        context.Flight.Checkpoint();
        var pilot = context.Pilot;
        var result = bursts.Pulse(pilot.PilotId, pilot.ShipId, module, effect, context.Flight.CommandBurstCandidates(), now);
        var activation = new DogmaActiveEffect(
            module,
            pilot.PilotId,
            pilot.ShipId,
            null,
            effect,
            now,
            result.Module.CycleTicks / (double)TimeSpan.TicksPerMillisecond,
            repeats
        );
        _cycles[module] = new(activation, result.ReadyAt);
        if (!repeating)
        {
            context.Notifications.Add(new ShipEffectChanged(activation, now, true, true));
        }
        MagazineWire.NotifyQuantity(context, result.Magazine, result.Magazine.Quantity, now);
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(pilot.PilotId, new DogmaItemKey.Item(pilot.ShipId), DogmaAttributeId.CapacitorCharge, now, result.Capacitor),
            ])
        );
        context.Notifications.Add(new FleetBoosted(result.Module.TypeId, result.Recipients.Length));
        context.Flight.NotifyCommandBurstPulse(result);
        Refresh(context, now);
    }

    private sealed record FleetBoosted(int ModuleType, int Recipients) : PilotNotification("OnFleetBoosted")
    {
        public override object?[] Arguments => [ModuleType, Recipients];
    }
}
