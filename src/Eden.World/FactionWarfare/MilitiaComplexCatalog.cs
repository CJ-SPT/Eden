using System.Collections.Concurrent;
using Eden.Data.Catalog;
using Eden.Data.Catalog.FactionWarfare;
using Eden.Gameplay.Agents;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
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
    TimeSpan DefenderInterval
);

internal sealed class MilitiaComplexCatalog(string catalogPath)
{
    private readonly ConcurrentDictionary<int, Lazy<MilitiaComplexProfile?>> _profiles = new();

    internal MilitiaComplexProfile? Read(MilitiaComplexVariant variant, MilitiaFaction occupier) =>
        _profiles.GetOrAdd(variant.Dungeon(occupier), id => new(() => Load(variant, occupier, id))).Value;

    private MilitiaComplexProfile? Load(MilitiaComplexVariant variant, MilitiaFaction occupier, int dungeonId)
    {
        using CatalogDbContext catalog = new(catalogPath);
        var dungeon = catalog.Read(CatalogData.Client.Dungeons, (long)dungeonId);
        if (dungeon is null)
        {
            return null;
        }

        var captureRoom = dungeon.Rooms!.Values.Single(room => room.Objects!.Values.Any(o => o.TypeID == variant.BeaconType));
        var objects = captureRoom.Objects!.Values.ToArray();
        var point = objects.Single(o => o.TypeID == CapturePoint(occupier));
        var beacon = objects.Single(o => o.TypeID == variant.BeaconType);
        var spawn = objects.Single(o => o.TypeID == variant.DefenderSpawner);
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
            Body(point),
            Body(beacon),
            gate,
            access,
            Position(spawn),
            TimeSpan.FromSeconds(variant.DefenderSpawner == 92671 ? 85 : 210)
        );

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
                    MilitiaComplexFamily.Large or MilitiaComplexFamily.Open => 3,
                    _ => throw new ArgumentOutOfRangeException(nameof(family)),
                }
            );
    }
}
