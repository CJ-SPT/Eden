using Eden.Data.World.Docking;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Runtime.Combat;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private sealed record WingWarp(long ShipId, SpacePosition Destination, double ArrivalSeconds);

    private WingWarp[] ScoutBackup(RoamingGang gang, long anchorId)
    {
        if (Live is not { } live)
        {
            return [];
        }
        List<WingWarp> inbound = [];
        foreach (var member in gang.Members)
        {
            if (
                member.PilotId == pilotId
                || !member.DamageCapable
                || member.Status != RoamingGangMemberStatus.Active
                || live.HaulerCheckpoints.GetValueOrDefault(member.PilotId)?.Work is not { } wing
                || wing.ShipId != member.ShipId
                || wing.TargetId != anchorId
                || wing.Navigation.Warp is not { } warp
                || live.Economy.Peek<FlightPosition>(member.ShipId)?.SystemId != Pilot.SystemId
                || ships.FindPilot(CapsuleerCommunityStore.MinerAccountId, member.PilotId) is not { } pilot
                || CapsuleerScheduleRules.WarpDeadline(wing.Navigation, pilot.Flight, wing.ClockOffsetTicks, wing.ActiveTicks)
                    is not { } arrival
            )
            {
                continue;
            }
            inbound.Add(new(member.ShipId, warp.Destination, Math.Max(0, (arrival - Work.ActiveTicks) / (double)TimeSpan.TicksPerSecond)));
        }
        return inbound.ToArray();
    }

    private double ScoutHold(RoamingGangMember scout, CapsuleerDefenseProfile profile, double opposition)
    {
        var current = health.Read(pilotId, Now);
        double tank = (current.Remaining.Shield + current.Remaining.Armor) / Math.Max(1, current.Defense.Shield + current.Defense.Armor);
        var ship = LiveCombatRuntime.Find(database.Path)?.DefenseShip(pilotId, Now);
        if (ship is null)
        {
            return 0;
        }
        double capacitor = ChargedResource.Recharged(
            ship.Capacitor,
            ship.CapacitorCapacity,
            ship.CapacitorRechargeMilliseconds,
            Math.Max(0, (Now - ship.CapacitorAt) / (double)TimeSpan.TicksPerMillisecond)
        );
        double budget = Math.Max(0, capacitor - ship.CapacitorCapacity * CapsuleerDefenseRules.OffensiveCapacitorReserve);
        double duration = profile
            .Modules.Where(module => module.Effect is DogmaEffect.WarpDisruptor or DogmaEffect.WarpScrambler)
            .Select(module =>
                Math.Floor(budget / Math.Max(1, module.Attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed)))
                * module.Attributes.GetValueOrDefault(DogmaAttributeId.Duration)
                / 1_000
            )
            .DefaultIfEmpty(0)
            .Max();
        return RoamingGangRules.ScoutHoldSeconds(tank, opposition, PvpTactics.Threat(scout.Hull), duration);
    }
}
