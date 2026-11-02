using Eden.Protocol;
using Eden.Protocol.Skills;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotSkillReader(WorldDatabase database) : IPilotSkillReader
{
    public IReadOnlyList<TrainedSkill> ReadSkills(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        var points = world.Set<PilotSkill>().Where(s => s.PilotId == pilotId).ToDictionary(s => s.TypeId, s => s.Points);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var types = points.Keys.ToArray();
        // Dogma attribute 275 is the skill's training time multiplier.
        var ranks = catalog.Set<TypeAttribute>().Where(a => a.AttributeId == 275 && types.Contains(a.ItemTypeId))
            .ToDictionary(a => a.ItemTypeId, a => a.Value);
        return points.Select(p => new TrainedSkill(p.Key, p.Value, ranks[p.Key])).ToArray();
    }
}
