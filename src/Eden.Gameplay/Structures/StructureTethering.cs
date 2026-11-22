using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Structures;

public sealed record TetherFlightState(bool InSpace, bool Warping, bool Cloaked, bool Disrupted, bool DisallowedModule);

public sealed record TetherEligibility(
    StructureProfile Structure,
    StructureState State,
    StructureUpkeepState Upkeep,
    bool HasCore,
    bool HasAccess,
    int ShipGroupId,
    double ShipRadius,
    SpacePosition ShipPosition,
    SpacePosition StructurePosition,
    double Range,
    bool HasOutgoingLocks,
    bool WeaponsTimer,
    bool Criminal,
    bool SecurityRestricted,
    long DelayedUntil,
    TetherFlightState Flight
);

public sealed record PilotTether(long ShipId, long StructureId, long EngagedAt, long RepairAt, bool RepairCompleted)
{
    public double StructureRadius { get; init; }
    public double Range { get; init; }
}

public interface IPilotTethering
{
    bool Available { get; }
    PilotTether? Advance(long pilotId, TetherFlightState flight);
    bool Protected(long shipId);
    void Release(long pilotId);
}

public static class StructureTetherRules
{
    // Eden's repair settlement policy; CCP documents the engagement-only check, not the server's repair cadence.
    public const long RepairDelay = 10 * TimeSpan.TicksPerSecond;

    public static bool Eligible(TetherEligibility input, long now) =>
        input.Flight.InSpace
        && !input.Flight.Warping
        && !input.Flight.Cloaked
        && !input.Flight.Disrupted
        && !input.Flight.DisallowedModule
        && input.HasCore
        && input.HasAccess
        && input.Upkeep != StructureUpkeepState.Abandoned
        && input.State
            is StructureState.Onlining
                or StructureState.ShieldVulnerable
                or StructureState.ArmorReinforced
                or StructureState.ArmorVulnerable
                or StructureState.HullReinforced
                or StructureState.HullVulnerable
                or StructureState.FobInvulnerable
        && StructureRules.CanDock(input.Structure, input.State, input.ShipGroupId)
        && !input.HasOutgoingLocks
        && !input.WeaponsTimer
        && !input.Criminal
        && !input.SecurityRestricted
        && input.DelayedUntil <= now
        && double.IsFinite(input.Range)
        && input.Range > 0
        && double.IsFinite(input.ShipRadius)
        && input.ShipRadius >= 0
        && input.ShipPosition.DistanceTo(input.StructurePosition) <= input.Structure.Radius + input.ShipRadius + input.Range;

    public static bool SecurityPermits(double systemSecurity, double pilotSecurity)
    {
        if (!double.IsFinite(systemSecurity) || !double.IsFinite(pilotSecurity))
        {
            return false;
        }
        if (systemSecurity < StructureRules.HighSecurityBoundary)
        {
            return true;
        }
        // CCP's Uprising thresholds: -2 at 1.0, falling by 0.5 per displayed security band through -4.5 at 0.5.
        const double HighestSecurityThreshold = -2;
        const double ThresholdPerBand = 0.5;
        const int HighestBand = 10;
        const int LowestBand = 5;
        double band = Math.Clamp(Math.Round(systemSecurity * HighestBand, MidpointRounding.AwayFromZero), LowestBand, HighestBand);
        return pilotSecurity > HighestSecurityThreshold - (HighestBand - band) * ThresholdPerBand;
    }
}
