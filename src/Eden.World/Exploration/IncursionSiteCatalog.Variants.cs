using System.Collections.Immutable;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;

namespace Eden.World.Exploration;

internal static partial class IncursionSiteCatalog
{
    internal const int ForwardReconnaissanceOutpost = 5188;
    internal const int PropagandaCluster = 5190;
    internal const int NationIndustrialProxy = 5191;
    internal const int NationMiningColony = 5165;
    internal const int OverrideTransferArray = 5164;
    internal const int OverwhelmedCivilianFacility = 5167;
    internal const int NationConsolidationNetwork = 5172;
    internal const int TruePowerProvisionalHeadquarters = 5114;
    internal const int TrueCreationsResearchCenter = 5175;
    internal const int Uroborus = 5201;

    private const int IndustrialProxyEntry = 2894;
    private const int Uitra = 2208;
    private const int UroborusFlagship = 3071;
    private const int Jel = 3524;
    private const int Youl = 3525;
    private const int Orkashu = 3526;
    private const int Raa = 3527;
    internal const int ReinforcedNationOutpost = 3500;

    private static readonly ImmutableArray<Design> s_variants =
    [
        new(
            IncursionSiteKind.Scout,
            ForwardReconnaissanceOutpost,
            ScoutArchetype,
            "Forward Reconnaissance Outpost",
            null,
            [
                new(new(-28_000, 9_000, 10_000), new("Intercept the scouts", [Group(Jel, 2, 4), Group(Youl, 1, 2), Group(Orkashu)])),
                new(new(27_000, 8_000, -19_000), new("Destroy the counterattack", [Group(Uitra), Group(Youl, 2, 3), Group(Jel, 3, 4)], 30)),
            ],
            Slot: 107
        ),
        new(
            IncursionSiteKind.Scout,
            PropagandaCluster,
            ScoutArchetype,
            "Propaganda Cluster",
            null,
            [
                new(new(25_000, -10_000, -8_000), new("Break the relay escort", [Group(Raa, 1, 1, true), Group(Jel, 3, 4), Group(Youl)])),
                new(new(-17_000, 21_000, 21_000), new("Stop the broadcast wing", [Group(Antem), Group(Orkashu, 2, 3), Group(Youl, 2, 3)])),
            ],
            Slot: 108
        ),
        new(
            IncursionSiteKind.Scout,
            NationIndustrialProxy,
            ScoutArchetype,
            "Nation Industrial Proxy",
            null,
            [
                new(new(13_000, 4_000, 30_000), new("Stop the belt patrol", [Group(Jel, 3, 5), Group(Orkashu), Group(Youl, 1, 2)])),
                new(
                    new(-30_000, 14_000, -3_000),
                    new("Intercept the industrial escort", [Group(Uitra, 2, 3), Group(Raa), Group(Jel, 2, 3)])
                ),
            ],
            Slot: 109,
            EntryType: IndustrialProxyEntry
        ),
        new(
            IncursionSiteKind.Vanguard,
            NationMiningColony,
            VanguardArchetype,
            "Nation Mining Colony",
            new(10_000, 4_000, 6_000),
            [
                new(
                    new(34_000, 18_000, 22_000),
                    new("Clear access to the ore field", [Group(Tama, 2, 4), Group(Schmaeel, 2, 3), Group(Romi)])
                ),
                new(
                    new(-38_000, 3_000, 13_000),
                    new("Dislodge the refinery escort", [Group(Auga, 2, 4), Group(Eystur, 3, 5), Group(Deltole)])
                ),
                new(new(19_000, 11_000, -44_000), new("Secure the ore refinery", [Group(Niarja, 2, 3), Group(Tama, 3, 5), Group(Outuni)])),
            ],
            Slot: 110
        ),
        new(
            IncursionSiteKind.Vanguard,
            OverrideTransferArray,
            VanguardArchetype,
            "Override Transfer Array",
            new(-13_000, 2_000, -5_000),
            [
                new(new(-30_000, 14_000, 11_000), new("Reach the logistics array", [Group(Auga), Group(Schmaeel, 3, 4), Group(Deltole)])),
                new(
                    new(31_000, -2_000, 29_000),
                    new("Eliminate the relay defense", [Group(Niarja, 1, 2), Group(Auga, 2, 3), Group(Romi)], 40)
                ),
                new(
                    new(-22_000, -14_000, -38_000),
                    new("Sweep the override fleet", [Group(Deltole, 2, 3), Group(Tama, 3, 5), Group(Eystur, 3, 4)])
                ),
            ],
            Slot: 111
        ),
        new(
            IncursionSiteKind.Assault,
            OverwhelmedCivilianFacility,
            AssaultArchetype,
            "Overwhelmed Civilian Facility",
            new(8_000, 7_000, 11_000),
            [
                new(
                    new(26_000, 30_000, -13_000),
                    new("Reach the civilian transport", [Group(Antem, 3, 5), Group(Schmaeel, 3, 4), Group(Romi, 2, 3)])
                ),
                new(
                    new(-56_000, 20_000, -12_000),
                    new("Clear the rescue route", [Group(Ostingele, 3, 4), Group(Niarja, 2, 4), Group(Tama, 3, 4)])
                ),
                new(
                    new(44_000, -11_000, 32_000),
                    new("Break the pursuit line", [Group(Deltole, 3, 4), Group(Youl, 2, 3), Group(Auga, 2, 3)])
                ),
            ],
            Slot: 112
        ),
        new(
            IncursionSiteKind.Assault,
            NationConsolidationNetwork,
            AssaultArchetype,
            "Nation Consolidation Network",
            new(10_000, 3_000, -9_000),
            [
                new(
                    new(32_000, 18_000, -22_000),
                    new(
                        "Clear the linked defense wings",
                        [Group(Ostingele, 3, 4), Group(Auga, 3, 4), Group(Yulai, 2, 3), Group(Niarja, 2, 4)]
                    ),
                    Grid: 0,
                    AlternateGrid: 1
                ),
                new(
                    new(-46_000, 3_000, 18_000),
                    new("Breach the relay pockets", [Group(Deltole, 3, 4), Group(Tama, 4, 5), Group(Intaki, 2, 3), Group(Romi, 2, 3)]),
                    Grid: 2,
                    AlternateGrid: 3
                ),
                new(
                    new(25_000, -25_000, 48_000),
                    new(
                        "Defeat the reserve wings",
                        [Group(Antem, 3, 4), Group(Ostingele, 3, 5), Group(Schmaeel, 4, 5), Group(Yulai, 2, 3)]
                    ),
                    Grid: 4,
                    AlternateGrid: 5
                ),
                new(
                    new(-48_000, 5_000, 18_000),
                    new("Enter the network command room", [Group(Outuni, 2, 3), Group(Deltole, 3, 4), Group(Renyn, 3, 5)]),
                    Grid: 6
                ),
            ],
            Slot: 113,
            Grids: [Grid(1), Grid(1, 1), Grid(2), Grid(2, 1), Grid(3), Grid(3, 1), Grid(4)],
            GatePlans:
            [
                new(1011221, new(10_000, 3_000, -9_000), 0),
                new(1011279, new(-10_000, 6_000, 12_000), 1),
                new(1011240, new(19_000, -4_000, 11_000), 2, 1, 0),
                new(1011291, new(-20_000, 5_000, -10_000), 3, 1, 1),
                new(1011265, new(-12_000, -11_000, 20_000), 4, 2, 2),
                new(1011293, new(12_000, 11_000, -20_000), 5, 2, 3),
                new(1011295, new(19_000, -9_000, -17_000), 6, 3, 4),
                new(1011297, new(-19_000, 9_000, 17_000), 6, 3, 5),
            ]
        ),
        new(
            IncursionSiteKind.Headquarters,
            TruePowerProvisionalHeadquarters,
            HeadquartersArchetype,
            "True Power Provisional Headquarters",
            new(20_000, 5_000, 3_000),
            [
                new(
                    new(46_000, 8_000, 19_000),
                    new("Breach the first security room", [Group(Ostingele, 4, 6), Group(Tama, 5, 7), Group(Niarja, 2, 3)])
                ),
                new(
                    new(-55_000, 14_000, -18_000),
                    new("Break the reinforcement line", [Group(Outuni, 2, 4), Group(Antem, 4, 6), Group(Yulai, 3, 4)]),
                    Grid: 1
                ),
                new(
                    new(16_000, -20_000, -41_000),
                    new("Destroy the mobile headquarters", [Group(ReinforcedNationOutpost), Group(Ostingele, 3, 4), Group(Intaki, 2, 4)]),
                    Grid: 2
                ),
            ],
            Slot: 114,
            Grids: [Grid(1), Grid(2, -1), Grid(3)],
            GatePlans:
            [
                new(1009290, new(20_000, 5_000, 3_000), 0),
                new(1007044, new(-23_000, 15_000, -12_000), 1, 1, 0),
                new(1007056, new(24_000, -16_000, 17_000), 2, 2, 1),
            ]
        ),
        new(
            IncursionSiteKind.Headquarters,
            TrueCreationsResearchCenter,
            HeadquartersArchetype,
            "True Creations Research Center",
            new(-14_000, 8_000, 2_000),
            [
                new(
                    new(-49_000, -3_000, 20_000),
                    new("Secure the transfer platform", [Group(Deltole, 4, 6), Group(Outuni, 2, 3), Group(Auga, 3, 5)])
                ),
                new(
                    new(51_000, 16_000, 48_000),
                    new("Hold the shield control route", [Group(Yulai, 3, 5), Group(Ostingele, 4, 6), Group(Niarja, 3, 5)], 45)
                ),
                new(
                    new(-29_000, 14_000, -52_000),
                    new("Destroy the research center", [Group(ReinforcedNationOutpost), Group(Schmaeel, 4, 6)])
                ),
            ],
            Slot: 115
        ),
        new(
            IncursionSiteKind.Mothership,
            Uroborus,
            MothershipArchetype,
            "Uroborus",
            new(18_000, -8_000, 12_000),
            [
                new(
                    new(-40_000, 5_000, 55_000),
                    new("Break the low-security blockade", [Group(Ostingele, 4, 6), Group(Tama, 5, 7), Group(Yulai, 4, 6)])
                ),
                new(
                    new(63_000, 18_000, 20_000),
                    new("Clear the flagship reserve", [Group(Intaki, 3, 5), Group(Outuni, 3, 4), Group(Deltole, 4, 6)])
                ),
                new(
                    new(3_000, -27_000, -65_000),
                    new(
                        "Destroy Uroborus and its escort",
                        [Group(UroborusFlagship), Group(Auga, 3, 5), Group(Niarja, 3, 4), Group(Ostingele, 2, 4)]
                    )
                ),
            ],
            Slot: 116
        ),
    ];

    private static SpacePosition Grid(int x, int y = 0) => new(x * MissionGateRules.RoomSpacing, y * MissionGateRules.RoomSpacing, 0);
}
