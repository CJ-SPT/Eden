using System.Collections.Frozen;
using Eden.Data.Catalog;
using Eden.Data.Catalog.FactionWarfare;
using Eden.Gameplay.Agents;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;

namespace Eden.World.FactionWarfare;

internal sealed record MilitiaComplexProfile(
    MilitiaComplexVariant Variant,
    int DungeonId,
    int NameId,
    int Archetype,
    int Difficulty,
    StaticSpaceObject CapturePoint,
    StaticSpaceObject Beacon,
    StaticSpaceObject? Gate,
    MissionGateAccess? Access,
    SpacePosition Defender,
    TimeSpan DefenderInterval,
    MilitiaBattlefieldProfile? Battlefield = null
);

internal sealed record MilitiaBattlefieldProfile(
    IReadOnlyList<StaticSpaceObject> Points,
    StaticSpaceObject Scoreboard,
    InventoryTypeList Capturing,
    InventoryTypeList Blocking,
    SpacePosition DefenderRally,
    SpacePosition AttackerRally,
    SpacePosition NeutralRally
);

internal static class MilitiaComplexCatalog
{
    private static readonly FrozenDictionary<int, (MilitiaComplexVariant Variant, MilitiaFaction Occupier)> s_dungeons =
        MilitiaComplexVariants
            .All.Append(MilitiaComplexVariants.Battlefield)
            .SelectMany(variant => Enum.GetValues<MilitiaFaction>().Select(faction => (Variant: variant, Occupier: faction)))
            .ToFrozenDictionary(entry => entry.Variant.Dungeon(entry.Occupier));

    private static readonly CatalogDefinition<MilitiaComplexProfile?> s_profiles = new(
        "militia-complex-profile",
        (catalog, id) => Load(catalog, s_dungeons[id].Variant, s_dungeons[id].Occupier, id)
    );

    internal static MilitiaComplexProfile? Read(CatalogProvider catalog, MilitiaComplexVariant variant, MilitiaFaction occupier) =>
        catalog.Definition(s_profiles, variant.Dungeon(occupier));

    private static MilitiaComplexProfile? Load(
        CatalogDbContext catalog,
        MilitiaComplexVariant variant,
        MilitiaFaction occupier,
        int dungeonId
    )
    {
        var dungeon = catalog.Read(CatalogData.Client.Dungeons, (long)dungeonId);
        if (dungeon is null)
        {
            return null;
        }

        var captureRoom = dungeon.Rooms!.Values.Single(room => room.Objects!.Values.Any(o => o.TypeID == variant.BeaconType));
        var objects = captureRoom.Objects!.Values.ToArray();
        var battlefield = variant.Family == MilitiaComplexFamily.Battlefield ? Battlefield() : null;
        var point = battlefield?.Points[0] ?? Body(objects.Single(o => o.TypeID == CapturePoint(occupier)));
        var beacon = objects.Single(o => o.TypeID == variant.BeaconType);
        var spawn = battlefield is null ? objects.Single(o => o.TypeID == variant.DefenderSpawner) : null;
        StaticSpaceObject? gate = null;
        MissionGateAccess? access = null;
        if (dungeon.Connections is { Length: 1 } connections)
        {
            var connection = connections[0];
            var source = dungeon.Rooms.Values.SelectMany(room => room.Objects!.Values).Single(o => o.ObjectID == connection.FromObjectID);
            gate = Body(source);
            access = new(
                CatalogTypeLists.Read(catalog, connection.AllowedShipsList!.Value),
                new HashSet<long>(),
                CatalogTypeLists.Read(catalog, connection.DisallowedFittedTypesList!.Value),
                null
            );
        }
        return new(
            variant,
            dungeonId,
            checked((int)dungeon.DungeonNameID!.Value),
            checked((int)dungeon.ArchetypeID!.Value),
            checked((int)dungeon.Difficulty!.Value),
            point,
            Body(beacon),
            gate,
            access,
            spawn is null ? SpacePosition.Zero : Position(spawn),
            TimeSpan.FromSeconds(variant.DefenderSpawner == 92671 ? 85 : 210),
            battlefield
        );

        MilitiaBattlefieldProfile Battlefield()
        {
            const int firstZoneA = 73443;
            const int lastZoneA = 73446;
            const int firstZoneB = 73854;
            const int lastZoneC = 73861;
            const int firstScoreboard = 73447;
            const int lastScoreboard = 73450;
            const int capturingHulls = 514;
            const int blockingHulls = 521;
            const int rallyBeacon = 10124;
            var points = objects
                .Where(o => o.TypeID is >= firstZoneA and <= lastZoneA or >= firstZoneB and <= lastZoneC)
                .OrderBy(o => o.TypeID)
                .Select(Body)
                .ToArray();
            if (points.Length != 3)
            {
                throw new InvalidDataException($"Battlefield {dungeonId} requires three capture points.");
            }
            // Pinned client 3662168 dungeon object name IDs identify each side's rally beacon and the neutral beacon.
            var (defender, attacker, neutral) = occupier switch
            {
                MilitiaFaction.Amarr => (638491, 638492, 638487),
                MilitiaFaction.Minmatar => (638500, 638501, 638496),
                MilitiaFaction.Caldari => (638510, 638511, 638506),
                MilitiaFaction.Gallente => (638519, 638520, 638515),
                _ => throw new ArgumentOutOfRangeException(nameof(occupier)),
            };
            return new(
                points,
                Body(objects.Single(o => o.TypeID is >= firstScoreboard and <= lastScoreboard)),
                CatalogTypeLists.Read(catalog, capturingHulls),
                CatalogTypeLists.Read(catalog, blockingHulls),
                Rally(defender),
                Rally(attacker),
                Rally(neutral)
            );

            SpacePosition Rally(int name) =>
                objects.SingleOrDefault(o => o.TypeID == rallyBeacon && o.ObjectNameID == name) is { Position: not null } marker
                    ? Position(marker)
                    : throw new InvalidDataException($"Battlefield {dungeonId} is missing rally point {name}.");
        }

        StaticSpaceObject Body(ClientDungeonsRoomsValueObjectsValue obj)
        {
            int typeId = checked((int)obj.TypeID!.Value);
            var type = catalog.RequireItem(typeId);
            return new(
                0,
                typeId,
                type.GroupId,
                type.CategoryId,
                MilitiaRules.Corporation(occupier),
                Position(obj),
                (float)(obj.Radius ?? type.Radius ?? 0)
            )
            {
                IsGlobal = false,
                Solid = type.GroupId == MissionGateRules.AccelerationGateGroup,
            };
        }
    }

    private static SpacePosition Position(ClientDungeonsRoomsValueObjectsValue obj) =>
        new(obj.Position?.X ?? 0, obj.Position?.Y ?? 0, obj.Position?.Z ?? 0);

    internal static int CapturePoint(MilitiaFaction occupier) =>
        occupier switch
        {
            MilitiaFaction.Amarr => 29311,
            MilitiaFaction.Caldari => 29421,
            MilitiaFaction.Gallente => 29422,
            MilitiaFaction.Minmatar => 29423,
            _ => throw new ArgumentOutOfRangeException(nameof(occupier)),
        };

    // The catalog gives spawn-table IDs without their rows. Use the pinned empire FW hull
    // cohorts as stand-ins for the server-authored fleet composition.
    internal static int DefenderType(MilitiaFaction occupier, MilitiaComplexFamily family)
    {
        int frigate = occupier switch
        {
            MilitiaFaction.Amarr => 75351,
            MilitiaFaction.Caldari => 75356,
            MilitiaFaction.Gallente => 75361,
            MilitiaFaction.Minmatar => 75366,
            _ => throw new ArgumentOutOfRangeException(nameof(occupier)),
        };
        return frigate
            + (
                family switch
                {
                    MilitiaComplexFamily.Scout => 0,
                    MilitiaComplexFamily.Small => 1,
                    MilitiaComplexFamily.Medium => 2,
                    MilitiaComplexFamily.Large or MilitiaComplexFamily.Open or MilitiaComplexFamily.Battlefield => 3,
                    _ => throw new ArgumentOutOfRangeException(nameof(family)),
                }
            );
    }
}
