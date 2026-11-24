using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;

namespace Eden.World.Exploration;

internal sealed record IncursionSiteDefinition(CombatAnomalyDefinition Encounter, StaticSpaceObject Gate, MissionGateAccess Access);

internal static class IncursionSiteCatalog
{
    internal const int NationCommanderOutpost = 5166;
    internal const int VanguardArchetype = 61;
    private const int EntryBeaconType = 10645;
    private const int AccelerationGateType = 17831;
    private const int CombatSceneryType = 29603;
    private const int TruePower = 1000162;

    // Build 3662168 supplies NPC identities and gate restrictions. The combat layout and wave composition are Eden's.
    private const int Renyn = 2190;
    private const int Romi = 2859;
    private const int Schmaeel = 2907;
    private const int Niarja = 2909;
    private const int Auga = 2936;
    private const int Tama = 2939;
    private const int Eystur = 2966;
    private const int Commander = 3492;

    internal static SpacePosition CombatOffset { get; } = new(MissionGateRules.RoomSpacing, 0, 0);

    private static readonly CatalogDefinition<IncursionSiteDefinition?> s_definition = new("incursion-vanguard", Load);

    internal static IncursionSiteDefinition? Read(CatalogDbContext catalog) => catalog.Definition(s_definition, NationCommanderOutpost);

    private static IncursionSiteDefinition? Load(CatalogDbContext catalog, int dungeonId)
    {
        var dungeon = catalog.Read(CatalogData.Client.Dungeons, (long)dungeonId);
        if (dungeon is null)
        {
            return null;
        }
        var connection = dungeon.Connections!.Value.Single();
        var gate = Body(catalog, connection.FromObjectID!.Value, AccelerationGateType, new(12_000, 4_000, -3_000));
        var beacon = Body(catalog, dungeon.EntryObjectID!.Value, EntryBeaconType, SpacePosition.Zero) with
        {
            IncursionBeacon = new(dungeonId, checked((int)dungeon.DungeonNameID!.Value), VanguardArchetype),
            OwnerId = (int)NpcPirateFaction.Sansha,
            Name = "Nation Commander Outpost",
            Solid = false,
            IsGlobal = true,
        };
        if (
            dungeon.ArchetypeID != VanguardArchetype
            || dungeon.FactionID != (int)NpcPirateFaction.Sansha
            || dungeon.EntryTypeID != EntryBeaconType
            || gate.GroupId != MissionGateRules.AccelerationGateGroup
            || connection.AllowedShipsList is not > 0
        )
        {
            throw new InvalidDataException("The pinned Nation Commander Outpost gate or dungeon profile differs.");
        }
        ImmutableArray<AnomalySpawnAnchor> anchors =
        [
            Anchor(1, new(24_000, 11_000, 18_000)),
            Anchor(2, new(-25_000, 19_000, -20_000)),
            Anchor(3, new(12_000, -21_000, -34_000)),
            Anchor(4, new(-15_000, -18_000, 28_000)),
        ];
        ImmutableArray<AnomalyEncounterWave> waves =
        [
            new("First wave", [Group(Romi), Group(Niarja), Group(Tama, 2, 3), Group(Eystur, 2, 4)]),
            new("Second wave", [Group(Auga, 1, 2), Group(Schmaeel, 2, 3), Group(Tama), Group(Renyn, 2, 3)]),
            new("Commander wave", [Group(Commander), Group(Romi), Group(Schmaeel), Group(Eystur, 2, 4)]),
            new("Final wave", [Group(Niarja, 1, 2), Group(Tama, 3, 5), Group(Schmaeel), Group(Renyn, 1, 2)]),
        ];
        ImmutableArray<StaticSpaceObject> objects =
        [
            beacon,
            gate,
            Body(catalog, 1, CombatSceneryType, CombatOffset + new SpacePosition(45_000, 0, -32_000)),
            Body(catalog, 2, CombatSceneryType, CombatOffset + new SpacePosition(-38_000, 12_000, 31_000)),
        ];
        return new(
            new(
                dungeonId,
                checked((int)dungeon.DungeonNameID!.Value),
                checked((int)dungeon.Difficulty!.Value),
                NpcPirateFaction.Sansha,
                TruePower,
                "Nation Commander Outpost",
                CombatOffset,
                waves,
                objects
            )
            {
                SpawnAnchors = anchors,
            },
            gate,
            MissionGateAccess.Default with
            {
                AllowedShips = CatalogTypeLists.Read(catalog, connection.AllowedShipsList.Value),
            }
        );
    }

    private static AnomalyNpcGroup Group(int type, int minimum = 1, int maximum = 1) => new(minimum, maximum, [type], false, false, false);

    private static AnomalySpawnAnchor Anchor(long id, SpacePosition position) =>
        new(id, CombatOffset + position, NpcEncounterDefinitions.VisibilityRange, true, true);

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
