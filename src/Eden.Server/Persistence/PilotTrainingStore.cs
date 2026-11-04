using System.Globalization;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Protocol.Skills;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotTrainingStore(WorldDatabase database, TimeProvider clock) : IPilotTraining
{
    // Build 3662168 appConst skill event IDs.
    private const int Started = 36;
    private const int Cancelled = 38;
    private const int Completed = 53;

    public TrainingState Advance(long pilotId) => Update(pilotId, null, false, false).Training;

    public TrainingState Save(long pilotId, IReadOnlyList<TrainingTarget> targets, bool activate) =>
        Update(pilotId, targets, activate, false).Training;

    public TrainingState Pause(long pilotId) => Update(pilotId, null, false, true).Training;

    internal CharacterRemapResult Remap(long pilotId, CharacterAttributes attributes) => Update(pilotId, null, false, false, attributes);

    public IReadOnlyList<TrainingEvent> History(long pilotId, int count)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<TrainingLog>()
            .Where(e => e.PilotId == pilotId)
            .OrderByDescending(e => e.Time)
            .ThenByDescending(e => e.Id)
            .Take(count)
            .Select(e => new TrainingEvent(e.Time, e.TypeId, e.Points, e.EventId))
            .ToArray();
    }

    private CharacterRemapResult Update(
        long pilotId,
        IReadOnlyList<TrainingTarget>? targets,
        bool activate,
        bool pause,
        CharacterAttributes? attributes = null
    )
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        bool remapped = false;
        if (attributes is not null)
        {
            var before = PilotCharacterSheetStore.ReadAttributes(world, pilotId);
            var after = CharacterSheetRules.Remap(before, attributes, now);
            remapped = after != before;
            if (remapped)
            {
                PilotCharacterSheetStore.GetOrCreate(world, pilotId).Apply(after);
            }
        }
        var saved = world.Set<QueuedLevel>().AsNoTracking().Where(q => q.PilotId == pilotId).OrderBy(q => q.Position).ToArray();
        List<TrainingEntry> queue = [];
        HashSet<int> changed = [];
        foreach (var row in saved)
        {
            var entry = new TrainingEntry(new(row.TypeId, row.Level), row.StartPoints, row.EndPoints, row.StartTime, row.EndTime);
            if (entry.EndTime is long end && end <= now)
            {
                world.Set<PilotSkill>().Find(pilotId, row.TypeId)!.Points = row.EndPoints;
                changed.Add(row.TypeId);
                Record(world, pilotId, end, row.TypeId, row.EndPoints, Completed);
                world.Remove(row);
            }
            else
            {
                queue.Add(entry);
            }
        }
        world.SaveChanges();
        var first = queue.FirstOrDefault();
        if (pause)
        {
            targets = queue.Select(e => e.Target).ToArray();
        }
        if (remapped)
        {
            targets = queue.Select(e => e.Target).ToArray();
            activate = first?.StartTime is not null;
        }
        if (targets is not null)
        {
            bool keepHead = !remapped && activate && first?.StartTime is not null && targets.Count > 0 && targets[0] == first.Target;
            if (!keepHead && first?.StartTime is not null)
            {
                long earned = first.PointsAt(now);
                world.Set<PilotSkill>().Find(pilotId, first.Target.TypeId)!.Points = earned;
                changed.Add(first.Target.TypeId);
                Record(world, pilotId, now, first.Target.TypeId, earned, Cancelled);
                world.SaveChanges();
            }
            queue = Plan(world, pilotId, targets, activate, keepHead ? first : null, now);
            world.Set<QueuedLevel>().Where(q => q.PilotId == pilotId).ExecuteDelete();
            for (int i = 0; i < queue.Count; i++)
            {
                var entry = queue[i];
                world.Add(
                    new QueuedLevel
                    {
                        PilotId = pilotId,
                        Position = i,
                        TypeId = entry.Target.TypeId,
                        Level = entry.Target.Level,
                        StartPoints = entry.StartPoints,
                        EndPoints = entry.EndPoints,
                        StartTime = entry.StartTime,
                        EndTime = entry.EndTime,
                    }
                );
            }
            if (activate && !keepHead && queue.Count > 0)
            {
                Record(world, pilotId, now, queue[0].Target.TypeId, queue[0].StartPoints, Started);
            }
        }
        // The next queued level starts at its predecessor's end, including while offline.
        if (saved.Length > 0 && changed.Count > 0)
        {
            foreach (var row in saved.Skip(1).Where(row => row.StartTime <= now && row.StartTime > saved[0].StartTime))
            {
                Record(world, pilotId, row.StartTime!.Value, row.TypeId, row.StartPoints, Started);
            }
        }
        world.SaveChanges();
        transaction.Commit();
        return new(new(queue, changed.ToArray()), remapped);
    }

    private List<TrainingEntry> Plan(
        WorldDbContext world,
        long pilotId,
        IReadOnlyList<TrainingTarget> targets,
        bool activate,
        TrainingEntry? running,
        long now
    )
    {
        if (targets.Count > TrainingRules.MaximumEntries)
        {
            throw new InvalidTrainingQueue("QueueTooManySkills");
        }
        List<TrainingEntry> result = [];
        if (targets.Count == 0)
        {
            return result;
        }
        var points = world.Set<PilotSkill>().Where(s => s.PilotId == pilotId).ToDictionary(s => s.TypeId, s => s.Points);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var owned = points.Keys.ToArray();
        var attributes = catalog
            .Set<TypeAttribute>()
            .Where(a =>
                owned.Contains(a.ItemTypeId)
                && (
                    a.AttributeId == (int)DogmaAttributeId.PrimaryAttribute
                    || a.AttributeId == (int)DogmaAttributeId.SecondaryAttribute
                    || a.AttributeId == (int)DogmaAttributeId.SkillTimeConstant
                )
            )
            .AsEnumerable()
            .GroupBy(a => a.ItemTypeId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.AttributeId, a => a.Value));
        int race = world.Pilots.Where(p => p.Id == pilotId).Select(p => p.RaceId).Single();
        var alpha = CatalogData
            .Client.Clonegrades.Get(catalog, race.ToString(CultureInfo.InvariantCulture))
            .Skills!.ToDictionary(s => s.TypeID!.Value, s => s.Level!.Value);
        var types = targets.Select(t => t.TypeId.ToString(CultureInfo.InvariantCulture)).Distinct().ToArray();
        var prerequisites = CatalogData
            .Client.Requiredskillsfortypes.Query(catalog)
            .Where(r => types.Contains(r.Key))
            .AsEnumerable()
            .ToDictionary(r => int.Parse(r.Key, CultureInfo.InvariantCulture), r => r.Value);
        var pilotAttributes = PilotCharacterSheetStore.ReadAttributes(world, pilotId).Attributes.ToDogma();
        var inputs = points.ToDictionary(
            p => p.Key,
            p =>
            {
                var dogma = attributes.GetValueOrDefault(p.Key, []);
                var required = prerequisites.GetValueOrDefault(p.Key);
                return new TrainingSkill(
                    p.Value,
                    dogma.GetValueOrDefault((int)DogmaAttributeId.SkillTimeConstant, 1),
                    pilotAttributes.GetValueOrDefault((DogmaAttributeId)dogma.GetValueOrDefault((int)DogmaAttributeId.PrimaryAttribute)),
                    pilotAttributes.GetValueOrDefault((DogmaAttributeId)dogma.GetValueOrDefault((int)DogmaAttributeId.SecondaryAttribute)),
                    checked((int)alpha.GetValueOrDefault(p.Key)),
                    required is null
                        ? []
                        : required
                            .Select(r => new TrainingTarget(int.Parse(r.Key, CultureInfo.InvariantCulture), checked((int)r.Value)))
                            .ToArray()
                );
            }
        );
        var grade = world.Pilots.Where(p => p.Id == pilotId).Select(p => p.Account.CloneGrade).Single();
        return TrainingRules.Plan(targets, new(inputs, grade), activate, running, now);
    }

    private void Record(WorldDbContext world, long pilotId, long time, int typeId, long points, int eventId)
    {
        if (eventId == Started)
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            PilotTaskTrackingStore.Record(world, catalog, pilotId, time, TaskEvents.TrainingStarted);
        }
        world.Add(
            new TrainingLog
            {
                PilotId = pilotId,
                Time = time,
                TypeId = typeId,
                Points = points,
                EventId = eventId,
            }
        );
    }
}
