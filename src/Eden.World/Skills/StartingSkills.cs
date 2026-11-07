using System.Globalization;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

internal static class StartingSkills
{
    internal static void Grant(WorldDbContext world, CatalogDbContext catalog, long pilotId, int raceId)
    {
        var levels =
            CatalogData.Client.Races.Get(catalog, raceId).Skills
            ?? throw new InvalidDataException($"Missing starting skills for race {raceId}.");
        foreach (var (key, level) in levels)
        {
            int typeId = int.Parse(key, CultureInfo.InvariantCulture);
            double rank = catalog
                .Set<TypeAttribute>()
                .Single(a => a.ItemTypeId == typeId && a.AttributeId == (int)DogmaAttributeId.SkillTimeConstant)
                .Value;
            long points = TrainedSkill.PointsForLevel(rank, checked((int)level));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPilotSkill(PilotId,TypeId,Points) VALUES ({pilotId},{typeId},{points})
                ON CONFLICT(PilotId,TypeId) DO UPDATE SET Points=MAX(Points,excluded.Points)
                """
            );
        }
    }
}
