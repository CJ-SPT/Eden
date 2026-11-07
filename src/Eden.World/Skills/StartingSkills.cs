using System.Globalization;
using Eden.Data.Catalog;
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
            catalog.Require(CatalogData.Client.Races, (long)raceId).Skills
            ?? throw new InvalidDataException($"Missing starting skills for race {raceId}.");
        foreach (var (key, level) in levels)
        {
            int typeId = int.Parse(key, CultureInfo.InvariantCulture);
            double rank = catalog.FittingAttributes(typeId)[DogmaAttributeId.SkillTimeConstant];
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
