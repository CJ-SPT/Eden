using System.Collections.Frozen;
using Eden.Data.Catalog;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Skills;

internal sealed partial class PilotSkillReader
{
    internal static int Level(WorldAction world, CatalogDbContext catalog, long pilotId, int skill)
    {
        long points = world.Set<PilotSkill>().Find(pilotId, skill)?.Points ?? 0;
        double rank = catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1);
        return new TrainedSkill(skill, points, rank).Level;
    }

    internal static IReadOnlyDictionary<int, int> Levels(WorldAction world, CatalogDbContext catalog, long pilotId, int[] skills)
    {
        var points = world
            .Set<PilotSkill>()
            .Where(s => s.PilotId == pilotId && Enumerable.Contains(skills, s.TypeId))
            .ToDictionary(s => s.TypeId, s => s.Points);
        // Find also sees pending added/modified entities in the owning transaction.
        foreach (
            var entry in world
                .ChangeTracker.Entries<PilotSkill>()
                .Where(e => e.Entity.PilotId == pilotId && Enumerable.Contains(skills, e.Entity.TypeId))
        )
        {
            points[entry.Entity.TypeId] = entry.Entity.Points;
        }
        return skills.ToDictionary(
            skill => skill,
            skill =>
                new TrainedSkill(
                    skill,
                    points.GetValueOrDefault(skill),
                    catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1)
                ).Level
        );
    }

    internal static void CheckRequirements(WorldAction world, CatalogDbContext catalog, long pilotId, int typeId)
    {
        var attributes = catalog.FittingAttributes(typeId);
        foreach (var (skillAttribute, levelAttribute) in InventoryDefinitions.RequiredSkills)
        {
            int skill = checked((int)attributes.GetValueOrDefault(skillAttribute));
            if (skill != 0 && Level(world, catalog, pilotId, skill) < attributes.GetValueOrDefault(levelAttribute))
            {
                throw new InvalidDataException("The pilot has not trained the required module skills.");
            }
        }
    }

    internal static IReadOnlySet<int> UsableTypes(WorldAction world, CatalogDbContext catalog, long pilotId, int[] types)
    {
        var attributes = types.Distinct().ToDictionary(type => type, catalog.FittingAttributes);
        int[] skills = attributes
            .Values.SelectMany(values =>
                InventoryDefinitions.RequiredSkills.Select(pair => checked((int)values.GetValueOrDefault(pair.Skill)))
            )
            .Where(skill => skill != 0)
            .Distinct()
            .ToArray();
        var levels = Levels(world, catalog, pilotId, skills);
        return attributes.Where(pair => SkillRequirementRules.Meets(pair.Value, levels)).Select(pair => pair.Key).ToFrozenSet();
    }
}
