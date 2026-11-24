using System.Collections.Frozen;
using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;

namespace Eden.World.Exploration;

internal sealed record IncursionSiteDefinition(
    CombatAnomalyDefinition Encounter,
    ImmutableArray<IncursionSiteGate> Gates,
    ImmutableArray<ImmutableArray<AnomalySpawnAnchor>> WaveAnchors,
    IncursionSiteKind Kind,
    int Slot,
    IncursionCargoObjective? Cargo
);

internal sealed record IncursionSiteGate(
    int Slot,
    StaticSpaceObject Body,
    SpacePosition Destination,
    int EarliestWave,
    MissionGateAccess Access
);

internal static partial class IncursionSiteCatalog
{
    internal const int DistressBeacon = 5193;
    internal const int NationCommanderOutpost = 5166;
    internal const int NationCommanderStronghold = 5168;
    internal const int NationRebirthFacility = 5171;
    internal const int KundaliniManifest = 5158;

    private const int ScoutArchetype = 60;
    private const int VanguardArchetype = 61;
    private const int AssaultArchetype = 62;
    private const int HeadquartersArchetype = 63;
    private const int MothershipArchetype = 64;

    private const int EntryBeaconType = 10645;
    private const int AccelerationGateType = 17831;
    private const int CombatSceneryType = 29603;
    private const int TruePower = 1000162;

    // Build 3662168 supplies NPC identity and admission. Eden defines geometry, rosters, and wave triggers.
    private const int Renyn = 2190;
    private const int Antem = 2191;
    private const int Kundalini = 2192;
    private const int Outuni = 2845;
    private const int Intaki = 2855;
    private const int Romi = 2859;
    private const int Schmaeel = 2907;
    private const int Niarja = 2909;
    private const int Ostingele = 2932;
    private const int Deltole = 2933;
    private const int Yulai = 2935;
    private const int Auga = 2936;
    private const int Tama = 2939;
    private const int Eystur = 2966;
    private const int Commander = 3492;

    internal static SpacePosition CombatOffset { get; } = new(MissionGateRules.RoomSpacing, 0, 0);

    private sealed record Stage(SpacePosition Position, AnomalyEncounterWave Wave, int Grid = 0, int AlternateGrid = -1);

    private sealed record GatePlan(long ObjectId, SpacePosition Position, int Destination, int RequiredWave = -1, int Origin = -1);

    private sealed record Design(
        IncursionSiteKind Kind,
        int DungeonId,
        int Archetype,
        string Name,
        SpacePosition? GatePosition,
        ImmutableArray<Stage> Stages,
        int Slot = 0,
        int EntryType = EntryBeaconType,
        ImmutableArray<SpacePosition> Grids = default,
        ImmutableArray<GatePlan> GatePlans = default
    );

    private static readonly ImmutableArray<Design> s_designs =
    [
        new(
            IncursionSiteKind.Scout,
            DistressBeacon,
            ScoutArchetype,
            "Distress Beacon",
            null,
            [
                new(new(18_000, 7_000, -12_000), new("Respond to the distress signal", [Group(Eystur, 2, 3), Group(Renyn, 1, 2)])),
                new(new(-23_000, -10_000, 20_000), new("Break the pursuit", [Group(Romi), Group(Eystur, 2, 3), Group(Niarja)])),
            ]
        ),
        new(
            IncursionSiteKind.Vanguard,
            NationCommanderOutpost,
            VanguardArchetype,
            "Nation Commander Outpost",
            new(12_000, 4_000, -3_000),
            [
                new(new(24_000, 11_000, 18_000), new("First wave", [Group(Romi), Group(Niarja), Group(Tama, 2, 3), Group(Eystur, 2, 4)])),
                new(
                    new(-25_000, 19_000, -20_000),
                    new("Second wave", [Group(Auga, 1, 2), Group(Schmaeel, 2, 3), Group(Tama), Group(Renyn, 2, 3)])
                ),
                new(
                    new(12_000, -21_000, -34_000),
                    new("Commander wave", [Group(Commander), Group(Romi), Group(Schmaeel), Group(Eystur, 2, 4)])
                ),
                new(
                    new(-15_000, -18_000, 28_000),
                    new("Final wave", [Group(Niarja, 1, 2), Group(Tama, 3, 5), Group(Schmaeel), Group(Renyn, 1, 2)])
                ),
            ]
        ),
        new(
            IncursionSiteKind.Assault,
            NationCommanderStronghold,
            AssaultArchetype,
            "Nation Commander Stronghold",
            new(14_000, -2_000, 6_000),
            [
                new(
                    new(44_000, -7_000, 10_000),
                    new("Break the interceptor screen", [Group(Auga, 2, 2, true), Group(Tama, 3, 5), Group(Romi, 2, 3)])
                ),
                new(
                    new(-21_000, 26_000, 49_000),
                    new(
                        "Remove the command escort",
                        [Group(Deltole, 2, 2, true), Group(Renyn, 3, 4), Group(Antem, 2, 3), Group(Niarja, 1, 2)]
                    )
                ),
                new(
                    new(-60_000, -16_000, -22_000),
                    new("Clear the stronghold", [Group(Commander), Group(Ostingele, 2, 4), Group(Schmaeel, 2, 3), Group(Yulai, 1, 2)])
                ),
            ]
        ),
        new(
            IncursionSiteKind.Headquarters,
            NationRebirthFacility,
            HeadquartersArchetype,
            "Nation Rebirth Facility",
            new(16_000, 3_000, -9_000),
            [
                new(
                    new(-35_000, 20_000, 15_000),
                    new("Engage the garrison", [Group(Ostingele, 3, 4), Group(Tama, 4, 5), Group(Deltole, 1, 2)])
                ),
                new(
                    new(57_000, 25_000, -52_000),
                    new("Intercept reinforcements", [Group(Antem, 4, 5), Group(Niarja, 2, 3), Group(Yulai, 3, 4)], 45)
                ),
                new(
                    new(13_000, -31_000, 43_000),
                    new("Clear the second defense line", [Group(Outuni, 2, 3), Group(Intaki, 2, 3), Group(Schmaeel, 4, 6)])
                ),
                new(
                    new(-68_000, 23_000, -28_000),
                    new("Defeat the Rebirth fleet", [Group(Deltole, 2, 4), Group(Ostingele, 4, 6), Group(Yulai, 2, 3)])
                ),
            ]
        ),
        new(
            IncursionSiteKind.Mothership,
            KundaliniManifest,
            MothershipArchetype,
            "The Kundalini Manifest",
            new(20_000, -4_000, 1_000),
            [
                new(
                    new(34_000, -7_000, 56_000),
                    new("Clear the flagship screen", [Group(Renyn, 4, 6), Group(Tama, 5, 7), Group(Outuni, 2, 3), Group(Yulai, 3, 4)])
                ),
                new(
                    new(-50_000, 40_000, 16_000),
                    new(
                        "Break the reserve escort",
                        [Group(Deltole, 3, 4), Group(Intaki, 2, 3), Group(Ostingele, 4, 5), Group(Niarja, 3, 4)]
                    )
                ),
                new(
                    new(-12_000, 40_000, -52_000),
                    new("Destroy the flagship", [Group(Kundalini), Group(Yulai, 3, 5), Group(Auga, 3, 4), Group(Schmaeel, 3, 5)])
                ),
            ]
        ),
    ];

    private static readonly Lazy<FrozenDictionary<int, Design>> s_designById = new(() =>
        s_designs.Concat(s_variants).ToFrozenDictionary(design => design.DungeonId)
    );

    private static readonly CatalogDefinition<ImmutableDictionary<int, IncursionSiteDefinition>> s_definitions = new(
        "incursion-sites",
        (catalog, _) =>
            s_designById
                .Value.Values.Select(design => (design.DungeonId, Definition: Load(catalog, design)))
                .Where(entry => entry.Definition is not null)
                .ToImmutableDictionary(entry => entry.DungeonId, entry => entry.Definition!)
    );

    internal static IncursionSiteKind? Kind(int dungeonId) => s_designById.Value.GetValueOrDefault(dungeonId)?.Kind;

    internal static IncursionSiteDefinition? Read(CatalogDbContext catalog, IncursionSiteKind kind = IncursionSiteKind.Vanguard) =>
        Read(catalog, s_designs.Single(site => site.Kind == kind).DungeonId);

    internal static IncursionSiteDefinition? Read(CatalogDbContext catalog, int dungeonId) =>
        catalog.GlobalDefinition(s_definitions).GetValueOrDefault(dungeonId);

    internal static IReadOnlyList<IncursionSiteDefinition> Variants(
        CatalogDbContext catalog,
        IncursionSiteKind kind,
        IncursionSecurity security
    )
    {
        var definitions = catalog.GlobalDefinition(s_definitions);
        if (kind == IncursionSiteKind.Mothership)
        {
            return definitions.GetValueOrDefault(security == IncursionSecurity.Low ? Uroborus : KundaliniManifest) is { } boss
                ? [boss]
                : [];
        }
        return definitions.Values.Where(site => site.Kind == kind).OrderBy(site => site.Slot).ToArray();
    }

    internal static bool IsMothershipFlagship(int dungeonId, int npcTypeId) =>
        (dungeonId, npcTypeId) is (KundaliniManifest, Kundalini) or (Uroborus, UroborusFlagship);

    internal static SpacePosition CombatCenter(WorldCombatAnomaly site)
    {
        var origin = new SpacePosition(site.X, site.Y, site.Z);
        return s_designById.Value.GetValueOrDefault(site.DungeonId) is { } design ? origin + Grids(design)[design.Stages[^1].Grid] : origin;
    }

    internal static ImmutableArray<SpacePosition> CombatGrids(int dungeonId) =>
        s_designById.Value.GetValueOrDefault(dungeonId) is { } design ? Grids(design) : [];

    private static ImmutableArray<SpacePosition> Grids(Design design) => design.Grids.IsDefault ? [Offset(design.Kind)] : design.Grids;

    internal static SpacePosition Offset(IncursionSiteKind? kind) =>
        kind is null or IncursionSiteKind.Scout ? SpacePosition.Zero : CombatOffset;

    internal static bool IsGated(int? archetype) => archetype is >= VanguardArchetype and <= MothershipArchetype;

    private static IncursionSiteDefinition? Load(CatalogDbContext catalog, Design design)
    {
        var dungeon = catalog.Read(CatalogData.Client.Dungeons, (long)design.DungeonId);
        if (dungeon is null)
        {
            return null;
        }
        if (
            dungeon.ArchetypeID != design.Archetype
            || dungeon.FactionID != (int)NpcPirateFaction.Sansha
            || dungeon.EntryTypeID != design.EntryType
            || dungeon.EntryObjectID is not long entry
            || dungeon.DungeonNameID is not long name
            || dungeon.Difficulty is not long difficulty
        )
        {
            throw new InvalidDataException($"The pinned Incursion dungeon profile {design.DungeonId} differs.");
        }
        var beacon = Body(catalog, entry, design.EntryType, SpacePosition.Zero) with
        {
            IncursionBeacon = new(design.DungeonId, checked((int)name), design.Archetype),
            OwnerId = (int)NpcPirateFaction.Sansha,
            Name = design.Name,
            Solid = false,
            IsGlobal = design.DungeonId != NationIndustrialProxy,
        };
        var grids = Grids(design);
        var gates = ImmutableArray.CreateBuilder<IncursionSiteGate>();
        var objects = ImmutableArray.CreateBuilder<StaticSpaceObject>();
        objects.Add(beacon);
        var connections = dungeon.Connections ?? [];
        var gatePlans = design.GatePlans;
        if (gatePlans.IsDefault)
        {
            gatePlans =
                design.GatePosition is { } position && connections.Length == 1 && connections[0].FromObjectID is long gateId
                    ? [new(gateId, position, 0)]
                    : [];
        }
        if (gatePlans.Length != connections.Length)
        {
            throw new InvalidDataException($"The pinned Incursion gate count for {design.DungeonId} differs.");
        }
        foreach (var plan in gatePlans)
        {
            var connection = connections.Single(source => source.FromObjectID == plan.ObjectId);
            var source = (plan.Origin < 0 ? SpacePosition.Zero : grids[plan.Origin]) + plan.Position;
            var gate = Body(catalog, plan.ObjectId, AccelerationGateType, source);
            if (gate.GroupId != MissionGateRules.AccelerationGateGroup)
            {
                throw new InvalidDataException($"The pinned Incursion gate type for {design.DungeonId} differs.");
            }
            var access = connection.AllowedShipsList is long shipList
                ? MissionGateAccess.Default with
                {
                    AllowedShips = CatalogTypeLists.Read(catalog, shipList),
                }
                : MissionGateAccess.Default;
            gates.Add(new(gates.Count, gate, grids[plan.Destination], plan.RequiredWave, access));
            objects.Add(gate);
        }

        if (design.DungeonId == OverrideTransferArray)
        {
            objects.Add(
                Body(catalog, IncursionObjectIds.LogisticsArray, LogisticsArray, grids[0] + new SpacePosition(18_000, 3_000, -14_000))
            );
        }
        for (int grid = 0; grid < grids.Length; grid++)
        {
            objects.Add(Body(catalog, 1 + grid * 2, CombatSceneryType, grids[grid] + new SpacePosition(45_000, 0, -32_000)));
            objects.Add(Body(catalog, 2 + grid * 2, CombatSceneryType, grids[grid] + new SpacePosition(-38_000, 12_000, 31_000)));
        }
        return new(
            new(
                design.DungeonId,
                checked((int)name),
                checked((int)difficulty),
                NpcPirateFaction.Sansha,
                TruePower,
                design.Name,
                grids[0],
                design.Stages.Select(stage => stage.Wave).ToImmutableArray(),
                objects.ToImmutable()
            ),
            gates.ToImmutable(),
            design
                .Stages.Select(
                    (stage, index) =>
                    {
                        var primary = new AnomalySpawnAnchor(
                            index * 2 + 1,
                            grids[stage.Grid] + stage.Position,
                            NpcEncounterDefinitions.VisibilityRange,
                            true,
                            true
                        );
                        return stage.AlternateGrid < 0
                            ? ImmutableArray.Create(primary)
                            : [primary, primary with { ObjectId = index * 2 + 2, Position = grids[stage.AlternateGrid] - stage.Position }];
                    }
                )
                .ToImmutableArray(),
            design.Kind,
            design.Slot == 0 ? BaselineSlot(design.Kind) : design.Slot,
            Cargo(catalog, design.DungeonId)
        );
    }

    private static int BaselineSlot(IncursionSiteKind kind) =>
        kind switch
        {
            IncursionSiteKind.Vanguard => 102,
            IncursionSiteKind.Scout => 103,
            IncursionSiteKind.Assault => 104,
            IncursionSiteKind.Headquarters => 105,
            IncursionSiteKind.Mothership => 106,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static AnomalyNpcGroup Group(int type, int minimum = 1, int maximum = 1, bool trigger = false) =>
        new(minimum, maximum, [type], trigger, false, false);

    private static StaticSpaceObject Body(CatalogDbContext catalog, long id, int typeId, SpacePosition position)
    {
        var type = catalog.RequireItem(typeId);
        return new(
            id,
            type.Id,
            type.GroupId,
            type.CategoryId,
            TruePower,
            position,
            DungeonObjectGeometry.Radius(
                type.CategoryId,
                type.Radius,
                null,
                catalog.Require(CatalogData.Client.Types, (long)type.Id).Volume ?? 0
            )
        )
        {
            IsGlobal = false,
            Solid = type.GroupId != MissionGateRules.AccelerationGateGroup && DungeonObjectGeometry.IsSolid(type.GroupId),
        };
    }
}
