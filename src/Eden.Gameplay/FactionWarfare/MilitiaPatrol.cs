using Eden.Gameplay.Flight;

namespace Eden.Gameplay.FactionWarfare;

public sealed record MilitiaPatrolSite(
    long Id,
    int SystemId,
    MilitiaFaction Occupier,
    MilitiaComplexFamily Family,
    StaticSpaceObject Gate,
    StaticSpaceObject Beacon,
    StaticSpaceObject Capture
);

public static class MilitiaPatrolRules
{
    // Pinned 3662168 roster and NPC station directory. These stations support normal militia enlistment.
    public const int Aldranette = 30003837;
    public const int Nennamaila = 30045344;
    public const int AldranetteStation = 60011014;
    public const int NennamailaStation = 60015126;
    public const int MaximumPerFaction = 2;

    public static MilitiaFaction? StagingFaction(int stationId) =>
        stationId switch
        {
            AldranetteStation => MilitiaFaction.Gallente,
            NennamailaStation => MilitiaFaction.Caldari,
            _ => null,
        };
}
