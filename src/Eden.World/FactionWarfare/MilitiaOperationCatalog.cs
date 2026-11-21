using Eden.Data.Catalog.Exploration;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Catalog;

namespace Eden.World.FactionWarfare;

internal static class MilitiaOperationCatalog
{
    private const int FirstDungeon = 9904;
    private const int FirstStorage = 73427;
    private const int FirstDecoder = 73397;
    private const int FirstSplice = 73401;
    private const int StorageCount = 5;

    private static readonly CatalogDefinition<ExplorationDefinition?> Profiles = new("militia-operation-centers", Load);

    internal static ExplorationDefinition? Read(CatalogDbContext catalog, MilitiaFaction faction) =>
        catalog.Definition(Profiles, FirstDungeon + MilitiaAdvantageRules.EmpireIndex(faction));

    internal static ExplorationDefinition? Find(CatalogDbContext catalog, int dungeon) => catalog.Definition(Profiles, dungeon);

    internal static int Loot(MilitiaFaction faction, int index) =>
        (index % 2 == 0 ? FirstSplice : FirstDecoder) + MilitiaAdvantageRules.EmpireIndex(faction);

    private static ExplorationDefinition? Load(CatalogDbContext catalog, int id)
    {
        var dungeon = catalog.Read(CatalogData.Client.Dungeons, (long)id);
        if (dungeon is null)
        {
            return null;
        }
        int storageType = FirstStorage + id - FirstDungeon;
        var containers = dungeon
            .Rooms!.Values.Single()
            .Objects!.Values.Where(row => row.TypeID == storageType)
            .OrderBy(row => row.ObjectID)
            .Select(row => new ExplorationContainerDefinition(
                storageType,
                4,
                new(row.Position!.X!.Value, row.Position.Y!.Value, row.Position.Z!.Value)
            ))
            .ToArray();
        if (containers.Length != StorageCount)
        {
            throw new InvalidDataException($"Operation Center {id} requires five cryptograph containers.");
        }
        int name = (int)dungeon.DungeonNameID!.Value;
        return new(
            id,
            name,
            "Operation Center",
            (int)MilitiaAdvantageRules.EmpireAt(id - FirstDungeon),
            ExplorationKind.Data,
            1,
            (int)(dungeon.Difficulty ?? 4),
            containers
        );
    }
}
