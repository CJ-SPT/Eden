using Eden.Gameplay.Characters;

namespace Eden.Gameplay.Agents;

public static class MissionStandingRules
{
    // Eden uses the directly observed agent and corporation fractions from two
    // normal-mission outcomes as a baseline. Tranquility's full penalty tables are unavailable.
    // EVE.js build 3396210 evidence: Gone Berserk (2504) quit and The Disgruntled Employee (921) repeat decline.
    public const double QuitAgentFraction = 0.04089592555998041;
    public const double QuitCorporationFraction = 0.003505365;
    public const double DeclineAgentFraction = 0.017409576535370612;
    public const double DeclineCorporationFraction = 0.001492249;

    public static double DerivedFraction(double factionFraction, double relationship, double sizeRatio) =>
        Math.Clamp(factionFraction * (relationship / StandingRules.Maximum) * sizeRatio, -1, 1);

    public static double ApplyDerivedCap(double before, double after, double relationship) =>
        after > before ? Math.Max(before, Math.Min(after, relationship)) : Math.Min(before, Math.Max(after, relationship));
}
