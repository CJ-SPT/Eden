using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.World.Agents;
using Eden.World.Catalog;

namespace Eden.World.Loot;

internal static class NpcTagLootCatalog
{
    private static readonly string[] Grades =
    [
        "Copper",
        "Bronze",
        "Silver",
        "Brass",
        "Palladium",
        "Gold",
        "Electrum",
        "Crystal",
        "Platinum",
        "Diamond",
    ];

    private static readonly CatalogDefinition<ImmutableDictionary<NpcPirateFaction, ImmutableArray<int>>> s_tags = new(
        "loot/criminal-tags",
        (catalog, _) => Load(catalog),
        EstimatedBytes: 2048
    );

    internal static NpcLootDrop? Roll(CatalogDbContext catalog, NpcPirateFaction faction, NpcHullClass hull, long seed)
    {
        if (NpcTagLootRules.Grade(hull, seed) is not int grade || !catalog.Definition(s_tags, 0).TryGetValue(faction, out var types))
        {
            return null;
        }
        return new(types[grade], 1);
    }

    private static ImmutableDictionary<NpcPirateFaction, ImmutableArray<int>> Load(CatalogDbContext catalog)
    {
        var objectives = catalog
            .ReadMany(CatalogData.Client.Missions, MissionCatalog.FixedMissionIds)
            .Values.Select(mission => checked((int)mission.CourierMission!.ObjectiveTypeID!.Value))
            .ToHashSet();
        var names = objectives.Select(catalog.RequireItem).ToDictionary(type => type.Name, type => type.Id);
        var families = new[]
        {
            (NpcPirateFaction.Angels, "Angel"),
            (NpcPirateFaction.BloodRaiders, "Blood"),
            (NpcPirateFaction.Guristas, "Guristas"),
            (NpcPirateFaction.Sansha, "Sansha"),
            (NpcPirateFaction.Serpentis, "Serpentis"),
        };
        return families
            .Where(family => Grades.All(grade => names.ContainsKey($"{family.Item2} {grade} Tag")))
            .ToImmutableDictionary(
                family => family.Item1,
                family => Grades.Select(grade => names[$"{family.Item2} {grade} Tag"]).ToImmutableArray()
            );
    }
}
