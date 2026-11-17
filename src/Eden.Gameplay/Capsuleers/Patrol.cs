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
}

public static class PatrolRules
{
    public const int MaximumGrids = 4;
    public const double PursuitMeters = 75_000;

    public static bool Choose(long pilot, long sequence, int percent) =>
        TravelerRoutineRules.Mix((ulong)pilot ^ TravelerRoutineRules.Mix((ulong)sequence)) % 100 < (ulong)percent;

    public static bool Allowed(PatrolProgress patrol, double security, CapsuleerPopulationPolicy policy) =>
        patrol.Kind is PatrolKind.Roam or PatrolKind.Transit
            ? security <= 0 && policy.RoamingPvp
            : policy.GateCamps
                && (security <= 0 || policy.LowsecGateCamps && TrafficRules.SecurityBand(security) == TrafficSecurityBand.Low);

    public static int DwellSeconds(long pilot, long grid) => 18 + (int)(TravelerRoutineRules.Mix((ulong)(pilot ^ grid)) % 23);

    public static IEnumerable<PilotSighting> Candidates(DockedPilot pilot, SpacePosition anchor, PilotSighting[] visible) =>
        visible.Where(other =>
            other.ShipId != pilot.ShipId
            && other.PilotId != pilot.PilotId
            && other.Hull is not (HullClass.Capsule or HullClass.Unclassified)
            && (pilot.CorpId < CorporationRules.FirstCorporationId || pilot.CorpId != other.CorporationId)
            && (pilot.Identity.AllianceId is null || pilot.Identity.AllianceId != other.AllianceId)
            && other.Position.DistanceTo(anchor) <= PursuitMeters
            && other.Position.DistanceTo(pilot.Position!) <= PursuitMeters
        );
}
