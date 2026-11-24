using Eden.Gameplay.Configuration;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Capsuleers;

public enum PatrolKind
{
    Roam,
    GateCamp,
    Transit,
}

public sealed record PatrolProgress(PatrolKind Kind, int SystemId)
{
    public GateCamp? Camp { get; init; }
    public long NextLook { get; init; }
    public long LeaveGridAt { get; init; }
    public bool WatchingForGang { get; init; }
    public long CommittedAt { get; init; }
    public string? DecisionReason { get; init; }
}

public static class PatrolRules
{
    public const int MaximumGrids = 4;
    public const double PursuitMeters = 75_000;

    // A narrow bearing alone cannot distinguish two grids in the same direction.
    public const double SearchConeRadians = Math.PI / 36;

    public static int ContactInterest(HullClass hull) =>
        hull switch
        {
            HullClass.Hauler or HullClass.BlockadeRunner or HullClass.DeepSpaceTransport => 2,
            HullClass.MiningBarge or HullClass.Exhumer or HullClass.ExpeditionFrigate => 2,
            HullClass.Capsule or HullClass.Unclassified => 0,
            _ => 1,
        };

    public static bool Choose(long pilot, long sequence, int percent) =>
        TravelerRoutineRules.Mix((ulong)pilot ^ TravelerRoutineRules.Mix((ulong)sequence)) % 100 < (ulong)percent;

    public static bool Allowed(PatrolProgress patrol, double security, CapsuleerPopulationPolicy policy) =>
        patrol.Kind is PatrolKind.Roam or PatrolKind.Transit
            ? security <= 0 && policy.RoamingPvp
            : policy.GateCamps
                && (security <= 0 || policy.LowsecGateCamps && TrafficRules.SecurityBand(security) == TrafficSecurityBand.Low);

    public static int DwellSeconds(long pilot, long grid) => 18 + (int)(TravelerRoutineRules.Mix((ulong)(pilot ^ grid)) % 23);

    public static long GridDeparture(long pilot, int system, long grid, long elapsed, RoamingGang? gang)
    {
        // Defenders hold the reported grid long enough for ships on both routes to arrive.
        if (
            gang is { Phase: RoamingGangPhase.Hunting, Regional: { Objective: RegionalObjective.Defend } order }
            && order.TargetSystemId == system
            && order.TargetGridId == grid
        )
        {
            return Math.Min(order.ExpiresAt, checked(elapsed + RoamingGangRules.RendezvousTicks));
        }
        return checked(elapsed + TimeSpan.FromSeconds(DwellSeconds(pilot, grid)).Ticks);
    }

    public static IEnumerable<PilotSighting> Candidates(DockedPilot pilot, SpacePosition anchor, PilotSighting[] visible) =>
        visible.Where(other =>
            other.ShipId != pilot.ShipId
            && other.PilotId != pilot.PilotId
            && other.Hull is not (HullClass.Capsule or HullClass.Unclassified)
            && !Friendly(pilot, other)
            && other.Position.DistanceTo(anchor) <= PursuitMeters
            && other.Position.DistanceTo(pilot.Position!) <= PursuitMeters
        );

    public static bool CorporateAllies(int left, int? leftAlliance, int right, int? rightAlliance) =>
        left >= CorporationRules.FirstCorporationId && left == right || leftAlliance is not null && leftAlliance == rightAlliance;

    public static bool Unaffiliated(int left, int right) =>
        left < CorporationRules.FirstCorporationId && right < CorporationRules.FirstCorporationId;

    public static bool Friendly(DockedPilot pilot, PilotSighting other, int? regionalGroup = null) =>
        CorporateAllies(pilot.CorpId, pilot.Identity.AllianceId, other.CorporationId, other.AllianceId)
        || Unaffiliated(pilot.CorpId, other.CorporationId) && regionalGroup is not null && regionalGroup == other.RegionalGroupId;
}
