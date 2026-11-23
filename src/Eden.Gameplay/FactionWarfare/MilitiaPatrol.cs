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
