using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Protocol.Skills;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotSkillReader(WorldDatabase database) : IPilotSkillReader
{
    internal static int Level(WorldDbContext world, CatalogDbContext catalog, long pilotId, int skill)
    {
        long points = world.Set<PilotSkill>().Find(pilotId, skill)?.Points ?? 0;
        double rank = catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1);
        return new TrainedSkill(skill, points, rank).Level;
    }

    internal static IReadOnlyDictionary<int, int> Levels(WorldDbContext world, CatalogDbContext catalog, long pilotId, int[] skills)
    {
        var points = world
            .Set<PilotSkill>()
            .Where(s => s.PilotId == pilotId && skills.Contains(s.TypeId))
            .ToDictionary(s => s.TypeId, s => s.Points);
        // Find also sees pending added/modified entities in the owning transaction.
        foreach (
            var entry in world
                .ChangeTracker.Entries<PilotSkill>()
                .Where(e => e.Entity.PilotId == pilotId && skills.Contains(e.Entity.TypeId))
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

    internal static void CheckRequirements(WorldDbContext world, CatalogDbContext catalog, long pilotId, int typeId)
    {
        var attributes = catalog
            .Set<TypeAttribute>()
            .Where(a => a.ItemTypeId == typeId)
            .ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);
        foreach (var (skillAttribute, levelAttribute) in InventoryDefinitions.RequiredSkills)
        {
            int skill = checked((int)attributes.GetValueOrDefault(skillAttribute));
            if (skill != 0 && Level(world, catalog, pilotId, skill) < attributes.GetValueOrDefault(levelAttribute))
            {
                throw new InvalidDataException("The pilot has not trained the required module skills.");
            }
        }
    }

    public IReadOnlyList<TrainedSkill> ReadSkills(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        var points = world.Set<PilotSkill>().Where(s => s.PilotId == pilotId).ToDictionary(s => s.TypeId, s => s.Points);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var types = points.Keys.ToArray();
        // Dogma attribute 275 is the skill's training time multiplier.
        var ranks = catalog
            .Set<TypeAttribute>()
            .Where(a => a.AttributeId == (int)DogmaAttributeId.SkillTimeConstant && types.Contains(a.ItemTypeId))
            .ToDictionary(a => a.ItemTypeId, a => a.Value);
        return points.Select(p => new TrainedSkill(p.Key, p.Value, ranks[p.Key])).ToArray();
    }
}
