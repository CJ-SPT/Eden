using System.Collections.Immutable;
using Eden.Gameplay.FactionWarfare;

namespace Eden.Data.Catalog.FactionWarfare;

public sealed record MilitiaMissionBinding(
    int MissionId,
    int Level,
    MilitiaFaction Enemy,
    int Frigate,
    int Escort,
    int Objective,
    int LandingBeacon
);

public static class MilitiaMissionBindings
{
    // Build 3662168 Decapitation, Knife in the Throat, A Left Hook and Lethal Strike.
    // The SDE exports room geometry and spawners without the server spawn-table rows.
    public static ImmutableArray<MilitiaMissionBinding> All { get; } =
    [
        new(13559, 1, MilitiaFaction.Amarr, 92128, 92129, 92130, 10124),
        new(13569, 1, MilitiaFaction.Minmatar, 92245, 92244, 92246, 10124),
        new(13581, 1, MilitiaFaction.Gallente, 92216, 92217, 92218, 10124),
        new(13591, 1, MilitiaFaction.Caldari, 92188, 92189, 92190, 10124),
        new(13600, 2, MilitiaFaction.Minmatar, 92245, 92255, 29180, 75920),
        new(13609, 2, MilitiaFaction.Gallente, 92216, 92227, 29179, 75920),
        new(13619, 2, MilitiaFaction.Caldari, 92188, 92199, 29178, 75920),
        new(13624, 2, MilitiaFaction.Amarr, 92128, 92137, 29166, 75920),
        new(13644, 3, MilitiaFaction.Gallente, 92216, 92231, 29176, 75922),
        new(13654, 3, MilitiaFaction.Caldari, 92188, 92203, 29175, 75922),
        new(13655, 3, MilitiaFaction.Minmatar, 92245, 92259, 29177, 75922),
        new(13659, 3, MilitiaFaction.Amarr, 92128, 92141, 29168, 75922),
        new(13699, 4, MilitiaFaction.Caldari, 92192, 92208, 92210, 10124),
        new(13703, 4, MilitiaFaction.Gallente, 92220, 92236, 92238, 10124),
        new(13717, 4, MilitiaFaction.Amarr, 92132, 92122, 92124, 10124),
        new(13726, 4, MilitiaFaction.Minmatar, 92248, 92263, 92266, 10124),
    ];
}
