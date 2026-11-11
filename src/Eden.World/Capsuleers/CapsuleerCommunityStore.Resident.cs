using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerCommunityStore
{
    internal static void GrantRequirements(WorldAction world, CatalogDbContext catalog, long pilot, int type, HashSet<int> visited)
    {
        if (!visited.Add(type))
        {
            return;
        }
        var attributes = catalog.FittingAttributes(type);
        foreach (var (skillAttribute, levelAttribute) in InventoryDefinitions.RequiredSkills)
        {
            int skill = (int)attributes.GetValueOrDefault(skillAttribute);
            if (skill == 0)
            {
                continue;
            }
            GrantRequirements(world, catalog, pilot, skill, visited);
            long points = TrainedSkill.PointsForLevel(
                catalog.FittingAttributes(skill)[DogmaAttributeId.SkillTimeConstant],
                (int)attributes.GetValueOrDefault(levelAttribute)
            );
            var saved = world.Set<PilotSkill>().Find(pilot, skill) ?? world.Add(new PilotSkill { PilotId = pilot, TypeId = skill }).Entity;
            saved.Points = Math.Max(saved.Points, points);
        }
    }
}
