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
    internal static IReadOnlyDictionary<int, ExpertVirtualSkill> VirtualSkills(WorldAction world, CatalogDbContext catalog, long pilotId) =>
        world is EconomyTransaction resident ? resident.ExpertSkills(catalog, pilotId) : EconomyExpertSkills.Empty.Grants;

    internal static int OperationalLevel(WorldAction world, CatalogDbContext catalog, long pilotId, int skill) =>
        Math.Max(Level(world, catalog, pilotId, skill), VirtualSkills(world, catalog, pilotId).GetValueOrDefault(skill)?.Level ?? 0);

    internal static IReadOnlyDictionary<int, int> OperationalLevels(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int[] skills,
        IReadOnlyDictionary<int, ExpertVirtualSkill>? grants = null
    )
    {
        grants ??= VirtualSkills(world, catalog, pilotId);
        return grants.Count == 0
            ? Levels(world, catalog, pilotId, skills)
            : skills.ToDictionary(
                skill => skill,
                skill => Math.Max(Level(world, catalog, pilotId, skill), grants.GetValueOrDefault(skill)?.Level ?? 0)
            );
    }

    internal static int Level(WorldAction world, CatalogDbContext catalog, long pilotId, int skill)
    {
        long points = world.SkillPoints(pilotId, skill) ?? 0;
        double rank = catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1);
        return new TrainedSkill(skill, points, rank).Level;
    }

    internal static IReadOnlyDictionary<int, int> Levels(WorldAction world, CatalogDbContext catalog, long pilotId, int[] skills)
    {
        if (world is EconomyTransaction)
        {
            return skills.ToDictionary(skill => skill, skill => Level(world, catalog, pilotId, skill));
        }
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
        IReadOnlyDictionary<int, ExpertVirtualSkill>? grants = null;
        foreach (var (skillAttribute, levelAttribute) in InventoryDefinitions.RequiredSkills)
        {
            int skill = checked((int)attributes.GetValueOrDefault(skillAttribute));
            double required = attributes.GetValueOrDefault(levelAttribute);
            if (skill != 0 && Level(world, catalog, pilotId, skill) < required)
            {
                grants ??= VirtualSkills(world, catalog, pilotId);
                if ((grants.GetValueOrDefault(skill)?.Level ?? 0) < required)
                {
                    throw new InvalidDataException("The pilot has not trained the required module skills.");
                }
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
        var levels = OperationalLevels(world, catalog, pilotId, skills);
        return attributes.Where(pair => SkillRequirementRules.Meets(pair.Value, levels)).Select(pair => pair.Key).ToFrozenSet();
    }
}
