using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

internal static partial class StartingSkills
{
    internal static void Grant(WorldAction world, CatalogDbContext catalog, long pilotId, int raceId)
    {
        var levels =
            catalog.Require(CatalogData.Client.Races, (long)raceId).Skills
            ?? throw new InvalidDataException($"Missing starting skills for race {raceId}.");
        foreach (var (key, level) in levels)
        {
            int typeId = int.Parse(key, CultureInfo.InvariantCulture);
            double rank = catalog.FittingAttributes(typeId)[DogmaAttributeId.SkillTimeConstant];
            long points = TrainedSkill.PointsForLevel(rank, checked((int)level));
            var saved =
                world.Set<PilotSkill>().Find(pilotId, typeId) ?? world.Add(new PilotSkill { PilotId = pilotId, TypeId = typeId }).Entity;
            saved.Points = Math.Max(saved.Points, points);
        }
    }
}
