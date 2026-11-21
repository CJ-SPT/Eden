using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fighters;

internal sealed partial class FighterDeckHandler
{
    private FighterInSpace[] _seen = [];
    private long _seenShip;
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    internal bool MicroJumpDue(PilotCallContext context) =>
        context.Selected
        && _seenShip == context.Pilot.ShipId
        && _seen.Any(squadron => squadron.Mobility is { Destination: not null } jump && jump.EndsAt <= Now);

    private PilotCallResult Command(PilotCallContext context, string method, object?[] args)
    {
        var flight = context.Flight;
        long? structure = context.ControlledStructureId;
        if (structure is not null && method is not ("LaunchFightersFromTubes" or "RecallFightersToTubes"))
        {
            return PilotCallResult.Unsupported;
        }
        if (structure is null && (!flight.CanTarget || flight.PreparingWarp || flight.MicroJumpSpooling))
        {
            return new PilotCallResult.Failure("Use fighter commands from your active ship outside warp.");
        }
        var pilot = context.Pilot;
        flight.Checkpoint();
        switch (method, args)
        {
            case ("LaunchFightersFromTubes", [var tubes]):
                var flags = ReadSelection(tubes).Select(flag => checked((int)flag)).ToArray();
                foreach (var launched in deck.Launch(pilot.PilotId, structure ?? pilot.ShipId, flags, Now))
                {
                    InventoryChangesWire.NotifyItemChange(context, launched.Inventory, beforeFlight: true);
                    context.NotificationsBeforeFlight.Add(new FighterTubeEmpty(launched.Squadron.Flag));
                }
                break;
            case ("RecallFightersToTubes", [var fighters]):
                var recall = ReadSelection(fighters);
                if (structure is long structureId && standup is not null)
                {
                    standup.Recall(pilot.PilotId, structureId, recall, Now);
                }
                else
                {
                    orders.OrderFighters(pilot.PilotId, recall, DroneOrder.ReturnToBay, null, new HashSet<long>(), Now);
                }
                Advance(context);
                flight.RefreshDrones();
                return new BlueMap<long, object?>(recall.ToDictionary(id => id, _ => (object?)null));
            case ("CmdActivateAbilitySlots", [var fighters, 0L, long target]):
                var attacking = ReadSelection(fighters);
                targeting.Advance(context);
                HashSet<long> locks = targeting.IsLocked(target) ? [target] : [];
                orders.OrderFighters(pilot.PilotId, attacking, DroneOrder.Engage, target, locks, Now);
                foreach (long id in attacking)
                {
                    context.NotificationsBeforeFlight.Add(new FighterAttackStarted(id, Now));
                }
                Advance(context);
                flight.RefreshDrones();
                return new BlueMap<long, object?>(attacking.ToDictionary(id => id, _ => (object?)null));
            case ("CmdDeactivateAbilitySlots", [var fighters, 0L]):
                var stopping = ReadSelection(fighters);
                orders.OrderFighters(pilot.PilotId, stopping, DroneOrder.Orbit, null, new HashSet<long>(), Now);
                Advance(context);
                flight.RefreshDrones();
                return new BlueMap<long, object?>(stopping.ToDictionary(id => id, _ => (object?)null));
            case ("CmdActivateAbilitySlots" or "CmdDeactivateAbilitySlots", [var fighters, (long)FighterMobilityRules.Slot]):
                var squads = ReadSelection(fighters);
                deck.SetMobility(pilot.PilotId, pilot.ShipId, squads, method == "CmdActivateAbilitySlots", Now);
                Advance(context);
                flight.RefreshDrones();
                return new BlueMap<long, object?>(squads.ToDictionary(id => id, _ => (object?)null));
            case ("CmdActivateAbilitySlots", [var fighters, (long)FighterMobilityRules.Slot, var point]):
                var jumping = ReadSelection(fighters);
                deck.SetMobility(pilot.PilotId, pilot.ShipId, jumping, true, Now, ReadPoint(point));
                Advance(context);
                flight.RefreshDrones();
                return new BlueMap<long, object?>(jumping.ToDictionary(id => id, _ => (object?)null));
            case ("CmdActivateAbilitySlots", [var fighters, (long)FighterDeckRules.SecondarySlot, long salvoTarget]):
                var firing = ReadSelection(fighters);
                targeting.Advance(context);
                orders.SetSecondaryAbility(pilot.PilotId, pilot.ShipId, firing, salvoTarget, targeting.IsLocked(salvoTarget), Now);
                Advance(context);
                flight.RefreshDrones();
                return new BlueMap<long, object?>(firing.ToDictionary(id => id, _ => (object?)null));
            case ("CmdActivateAbilitySlots", [var fighters, (long)FighterDeckRules.SecondarySlot, var aim]):
                var bombing = ReadSelection(fighters);
                orders.SetSecondaryAbility(pilot.PilotId, pilot.ShipId, bombing, null, false, Now, ReadPoint(aim));
                Advance(context);
                flight.RefreshMissiles();
                return new BlueMap<long, object?>(bombing.ToDictionary(id => id, _ => (object?)null));
            case ("CmdDeactivateAbilitySlots", [var fighters, (long)FighterDeckRules.SecondarySlot]):
                var ceasing = ReadSelection(fighters);
                orders.SetSecondaryAbility(pilot.PilotId, pilot.ShipId, ceasing, null, false, Now);
                Advance(context);
                return new BlueMap<long, object?>(ceasing.ToDictionary(id => id, _ => (object?)null));
            case ("ExecuteMovementCommandOnFighters", [var fighters, var instruction, long home, _])
                when BlueMarshal.Text(instruction) == "ORBIT" && home == pilot.ShipId:
                orders.OrderFighters(pilot.PilotId, ReadSelection(fighters), DroneOrder.Orbit, null, new HashSet<long>(), Now);
                Advance(context);
                flight.RefreshDrones();
                return PilotCallResult.None;
            default:
                return PilotCallResult.Unsupported;
        }
        Advance(context);
        flight.RefreshDrones();
        return BlueEmpty.Dictionary;
    }

    internal void Advance(PilotCallContext context)
    {
        if (
            !context.Selected
            || context.ControlledStructureId is null && !FighterDeckRules.Supports(context.Pilot.ShipGroupId, context.Pilot.ShipTypeId)
        )
        {
            _seen = [];
            _seenShip = 0;
            return;
        }
        var pilot = context.Pilot;
        long host = context.ControlledStructureId ?? pilot.ShipId;
        if (_seenShip != host)
        {
            _seen = [];
            _seenShip = host;
        }
        var snapshot = deck.Read(pilot.PilotId, host);
        if (_seen.SequenceEqual(snapshot.Squadrons))
        {
            return;
        }
        var present = snapshot.Squadrons.ToDictionary(squad => squad.SquadronId);
        var previous = _seen.ToDictionary(squad => squad.SquadronId);
        foreach (var squad in _seen)
        {
            if (squad.Activity == DroneActivity.Fighting && present.GetValueOrDefault(squad.SquadronId)?.Activity != DroneActivity.Fighting)
            {
                context.NotificationsBeforeFlight.Add(new FighterAttackEnded(squad.SquadronId));
            }
            if (squad.MobilityActive && present.GetValueOrDefault(squad.SquadronId)?.MobilityActive != true)
            {
                context.NotificationsBeforeFlight.Add(new FighterMobilityEnded(squad.SquadronId));
            }
            if (squad.SalvoActive && present.GetValueOrDefault(squad.SquadronId)?.SalvoActive != true)
            {
                context.NotificationsBeforeFlight.Add(new FighterSecondaryEnded(squad.SquadronId));
            }
            if (squad.Tackle is not null && present.GetValueOrDefault(squad.SquadronId)?.Tackle is null)
            {
                context.NotificationsBeforeFlight.Add(new FighterSecondaryEnded(squad.SquadronId));
            }
            if (!present.ContainsKey(squad.SquadronId))
            {
                context.NotificationsBeforeFlight.Add(new FighterRemoved(squad));
                var landed = snapshot.Tubes.SingleOrDefault(tube => tube.SquadronId == squad.SquadronId);
                if (landed is not null)
                {
                    context.NotificationsBeforeFlight.Add(new FighterTubeContent(landed));
                }
                context.NotificationsBeforeFlight.Add(new FighterTubeStatus(squad.Flag, landed is null ? "EMPTY" : "READY"));
            }
        }
        foreach (var squad in snapshot.Squadrons)
        {
            var old = previous.GetValueOrDefault(squad.SquadronId);
            if (old is null)
            {
                context.NotificationsBeforeFlight.Add(new FighterAdded(squad));
            }
            else if (old.Count != squad.Count)
            {
                context.NotificationsBeforeFlight.Add(new FighterCountChanged(squad));
            }
            if (squad is { MobilityActive: true, Mobility: { } cycle } && old?.Mobility != cycle)
            {
                context.NotificationsBeforeFlight.Add(new FighterMobilityStarted(squad.SquadronId, cycle));
            }
            if (squad is { SalvoActive: true, Salvo: { } salvo } && old?.Salvo?.StartedAt != salvo.StartedAt)
            {
                context.NotificationsBeforeFlight.Add(new FighterSalvoStarted(squad.SquadronId, salvo));
            }
            if (squad.Tackle is { } tackle && old?.Tackle != tackle)
            {
                context.NotificationsBeforeFlight.Add(new FighterTackleStarted(squad.SquadronId, tackle));
            }
            if (old is null || (old.Activity == DroneActivity.ReturningToBay) != (squad.Activity == DroneActivity.ReturningToBay))
            {
                context.NotificationsBeforeFlight.Add(
                    new FighterTubeStatus(squad.Flag, squad.Activity == DroneActivity.ReturningToBay ? "RECALLING" : "INSPACE")
                );
            }
        }
        _seen = snapshot.Squadrons.ToArray();
    }

    private static long[] ReadSelection(object? value)
    {
        IEnumerable<object?> items = value switch
        {
            object?[] tuple => tuple,
            List<object?> list => list,
            HashSet<long> set => set.Cast<object?>(),
            _ => throw new InvalidDataException("Choose a fighter selection."),
        };
        var selected = items
            .Select(item => item is long id && id > 0 ? id : throw new InvalidDataException("Choose valid fighter identities."))
            .ToArray();
        if (selected.Length is < 1 or > FighterDeckRules.MaximumTubes || selected.Distinct().Count() != selected.Length)
        {
            throw new InvalidDataException("Choose distinct fighter identities.");
        }
        return selected;
    }

    private static SpacePosition ReadPoint(object? raw)
    {
        var point = BlueRead.Tuple(raw is List<object?> list ? list.ToArray() : raw, 3);
        return new(Coordinate(point[0]), Coordinate(point[1]), Coordinate(point[2]));

        static double Coordinate(object? value) =>
            value switch
            {
                double number when double.IsFinite(number) => number,
                long number => number,
                _ => throw new InvalidDataException("Choose a finite fighter target point."),
            };
    }
}

internal sealed record FighterAdded(FighterInSpace Squadron) : PilotNotification("OnFighterAddedToController")
{
    public override object?[] Arguments =>
        [Squadron.SquadronId, Squadron.TypeId, Squadron.Flag, Squadron.Count, FighterAbilityState.For(Squadron)];
}

internal sealed record FighterRemoved(FighterInSpace Squadron) : PilotNotification("OnFighterRemovedFromController")
{
    public override object?[] Arguments => [Squadron.SquadronId, Squadron.Flag];
}

internal sealed record FighterCountChanged(FighterInSpace Squadron) : PilotNotification("OnInSpaceSquadronSizeChanged")
{
    public override object?[] Arguments => [Squadron.SquadronId, Squadron.Count];
}

internal sealed record FighterAttackStarted(long Squadron, long Start) : PilotNotification("OnFighterAbilitySlotActivated")
{
    public override object?[] Arguments => [Squadron, 0, null, Start, 5000, null];
}

internal sealed record FighterAttackEnded(long Squadron) : PilotNotification("OnFighterAbilitySlotDeactivated")
{
    public override object?[] Arguments => [Squadron, 0, null];
}

internal static class FighterAbilityState
{
    internal static List<object?> For(FighterInSpace squadron)
    {
        Dictionary<int, object?> charges = [];
        Dictionary<int, object?> cooldowns = [];
        if (squadron.Mobility is { } cycle)
        {
            cooldowns[FighterMobilityRules.Slot] = Cooldown(cycle);
        }
        if (squadron.Salvo is { } salvo)
        {
            charges[FighterDeckRules.SecondarySlot] = salvo.Charges;
            if (SalvoCooldown(salvo) is { } cooldown)
            {
                cooldowns[FighterDeckRules.SecondarySlot] = cooldown;
            }
        }
        return [new BlueMap<int, object?>(charges).ToBlue(), new BlueMap<int, object?>(cooldowns).ToBlue()];
    }

    internal static List<object?> Cooldown(FighterMobilityCycle cycle) => [cycle.EndsAt, cycle.ReadyAt];

    internal static List<object?>? SalvoCooldown(FighterSalvoCycle cycle) =>
        cycle.RearmAt > 0 ? [cycle.RearmAt - cycle.RearmTicks, cycle.RearmAt] : null;
}

internal sealed record FighterMobilityStarted(long Squadron, FighterMobilityCycle Cycle)
    : PilotNotification("OnFighterAbilitySlotActivated")
{
    public override object?[] Arguments =>
        [
            Squadron,
            FighterMobilityRules.Slot,
            null,
            Cycle.StartedAt,
            (Cycle.EndsAt - Cycle.StartedAt) / TimeSpan.TicksPerMillisecond,
            FighterAbilityState.Cooldown(Cycle),
        ];
}

internal sealed record FighterMobilityEnded(long Squadron) : PilotNotification("OnFighterAbilitySlotDeactivated")
{
    public override object?[] Arguments => [Squadron, FighterMobilityRules.Slot, null];
}

internal sealed record FighterSalvoStarted(long Squadron, FighterSalvoCycle Cycle) : PilotNotification("OnFighterAbilitySlotActivated")
{
    public override object?[] Arguments =>
        [
            Squadron,
            FighterDeckRules.SecondarySlot,
            Cycle.Charges,
            Cycle.StartedAt,
            (Cycle.EndsAt - Cycle.StartedAt) / TimeSpan.TicksPerMillisecond,
            FighterAbilityState.SalvoCooldown(Cycle),
        ];
}

internal sealed record FighterSecondaryEnded(long Squadron) : PilotNotification("OnFighterAbilitySlotDeactivated")
{
    public override object?[] Arguments => [Squadron, FighterDeckRules.SecondarySlot, null];
}

internal sealed record FighterTackleStarted(long Squadron, FighterTackleCycle Cycle) : PilotNotification("OnFighterAbilitySlotActivated")
{
    public override object?[] Arguments =>
        [
            Squadron,
            FighterDeckRules.SecondarySlot,
            null,
            Cycle.StartedAt,
            (Cycle.EndsAt - Cycle.StartedAt) / TimeSpan.TicksPerMillisecond,
            null,
        ];
}
