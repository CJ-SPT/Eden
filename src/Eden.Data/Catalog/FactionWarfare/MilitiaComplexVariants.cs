using System.Collections.Immutable;
using Eden.Gameplay.FactionWarfare;

namespace Eden.Data.Catalog.FactionWarfare;

public sealed record MilitiaComplexVariant(
    MilitiaComplexFamily Family,
    int Capacity,
    int BeaconType,
    int DefenderSpawner,
    int Caldari,
    int Minmatar,
    int Amarr,
    int Gallente,
    int? VictoryPoints = null,
    int? CaptureMinutes = null,
    int? BasePoints = null,
    bool Retired = false
)
{
    public MilitiaComplexRewardPolicy Policy
    {
        get
        {
            var standard = MilitiaComplexRules.Policy(Family, Capacity);
            return standard with
            {
                Minutes = CaptureMinutes ?? standard.Minutes,
                VictoryPoints = VictoryPoints ?? standard.VictoryPoints,
                BasePoints = BasePoints ?? standard.BasePoints,
            };
        }
    }

    public int Dungeon(MilitiaFaction faction) =>
        faction switch
        {
            MilitiaFaction.Caldari => Caldari,
            MilitiaFaction.Minmatar => Minmatar,
            MilitiaFaction.Amarr => Amarr,
            MilitiaFaction.Gallente => Gallente,
            _ => throw new ArgumentOutOfRangeException(nameof(faction)),
        };
}

public static class MilitiaComplexVariants
{
    // Build 3662168 client/dungeons and reactiveSpawnComponent occupier spawners.
    public static ImmutableArray<MilitiaComplexVariant> All { get; } =
    [
        new(MilitiaComplexFamily.Scout, 1, 92673, 92671, 14044, 14046, 14043, 14045, VictoryPoints: 50, CaptureMinutes: 5), // BSC-1
        new(MilitiaComplexFamily.Scout, 1, 75917, 90808, 9596, 9603, 9599, 9601, VictoryPoints: 25), // NVY-1
        new(MilitiaComplexFamily.Scout, 2, 75917, 90810, 9598, 9604, 9600, 9602, VictoryPoints: 25), // NVY-2
        new(MilitiaComplexFamily.Scout, 1, 89193, 90806, 13348, 13351, 13349, 13350), // ADV-1
        new(MilitiaComplexFamily.Small, 1, 75918, 90802, 9605, 9629, 9609, 9625, VictoryPoints: 50), // NVY-1
        new(MilitiaComplexFamily.Small, 2, 75918, 90804, 9607, 9631, 9611, 9627, VictoryPoints: 50), // NVY-2
        new(MilitiaComplexFamily.Small, 1, 75919, 90798, 9606, 9630, 9610, 9626, VictoryPoints: 75), // ADV-1
        new(MilitiaComplexFamily.Small, 2, 75919, 90800, 9608, 9632, 9612, 9628, VictoryPoints: 75), // ADV-2
        new(MilitiaComplexFamily.Small, 1, 93149, 93148, 14070, 14072, 14069, 14071, Retired: true), // Interceptor-1
        new(MilitiaComplexFamily.Small, 1, 97467, 97466, 14515, 14517, 14514, 14516, VictoryPoints: 150, BasePoints: 25_000), // AF-1
        new(MilitiaComplexFamily.Medium, 1, 75920, 90794, 9633, 9645, 9637, 9641), // NVY-1
        new(MilitiaComplexFamily.Medium, 3, 75920, 90796, 9635, 9647, 9639, 9643), // NVY-3
        new(MilitiaComplexFamily.Medium, 1, 75921, 90790, 9634, 9646, 9638, 9642, VictoryPoints: 175), // ADV-1
        new(MilitiaComplexFamily.Medium, 3, 75921, 90792, 9636, 9648, 9640, 9644, VictoryPoints: 175), // ADV-3
        new(MilitiaComplexFamily.Large, 1, 75922, 90786, 9649, 9662, 9652, 9659), // NVY-1
        new(MilitiaComplexFamily.Large, 4, 75922, 90788, 9651, 9664, 9654, 9661), // NVY-4
        new(MilitiaComplexFamily.Large, 1, 75923, 90784, 9650, 9663, 9653, 9660, VictoryPoints: 300), // ADV-1
        new(MilitiaComplexFamily.Open, 5, 75924, 91094, 9665, 9668, 9666, 9667),
    ];
}
