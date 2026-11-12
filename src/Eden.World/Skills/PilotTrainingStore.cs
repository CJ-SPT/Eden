using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

[Injectable]
internal sealed partial class PilotTrainingStore(WorldDatabase database, TimeProvider clock)
    : IPilotTraining,
        IPilotFreeSkillPoints,
        IPilotSkillGrants
{
    // Build 3662168 appConst skill event IDs.
    private const int Started = 36;
    private const int Cancelled = 38;
    private const int Completed = 53;

    internal TrainingState Advance(WorldDbContext world, long pilotId, long now) =>
        UpdateCore(world, pilotId, null, false, false, null, null, false, now).Training;

    public TrainingState Advance(long pilotId) => Update(pilotId, null, false, false).Training;

    public TrainingState Save(long pilotId, IReadOnlyList<TrainingTarget> targets, bool activate) =>
        Update(pilotId, targets, activate, false).Training;

    public TrainingState Pause(long pilotId) => Update(pilotId, null, false, true).Training;

    internal CharacterRemapResult Remap(long pilotId, CharacterAttributes attributes)
    {
        var result = Update(pilotId, null, false, false, attributes);
        return new(result.Training, result.Remapped);
    }

    internal TrainingState ChangeAttributes(
        WorldDbContext world,
        long pilotId,
        Action change,
        bool implantChange = true,
        long? effectiveTime = null
    ) => ChangeAttributes(new SqlWorldAction(world), pilotId, change, implantChange, effectiveTime);

    internal TrainingState ChangeAttributes(
        WorldAction world,
        long pilotId,
        Action change,
        bool implantChange = true,
        long? effectiveTime = null
    ) =>
        UpdateCore(
            world,
            pilotId,
            null,
            false,
            false,
            null,
            null,
            false,
            effectiveTime ?? clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
            change,
            implantChange
        ).Training;

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

    private TrainingUpdateResult Update(
        long pilotId,
        IReadOnlyList<TrainingTarget>? targets,
        bool activate,
        bool pause,
        CharacterAttributes? attributes = null,
        PointOperation? points = null,
        bool preview = false
    )
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var result = UpdateCore(world, pilotId, targets, activate, pause, attributes, points, preview, now);
        if (!preview)
        {
            transaction.Commit();
        }
        return result;
    }

    private TrainingUpdateResult UpdateCore(
        WorldDbContext world,
        long pilotId,
        IReadOnlyList<TrainingTarget>? targets,
        bool activate,
        bool pause,
        CharacterAttributes? attributes,
        PointOperation? points,
        bool preview,
        long now
    ) => UpdateCore(new SqlWorldAction(world), pilotId, targets, activate, pause, attributes, points, preview, now);

    private TrainingUpdateResult UpdateCore(
        WorldAction world,
        long pilotId,
        IReadOnlyList<TrainingTarget>? targets,
        bool activate,
        bool pause,
        CharacterAttributes? attributes,
        PointOperation? points,
        bool preview,
        long now,
        Action? changeAttributes = null,
        bool implantChange = true
    )
    {
        bool remapped = false;
        if (attributes is not null)
        {
            if (!world.Filter<Pilot>(pilot => pilot.Id == pilotId).Any())
            {
                throw new InvalidDataException("Unknown character.");
            }
            var sheet = world.Set<SavedCharacterSheet>().Find(pilotId);
            var before = sheet?.Snapshot() ?? NeuralRemapState.Default;
            var after = CharacterSheetRules.Remap(before, attributes, now);
            remapped = after != before;
            if (remapped)
            {
                (sheet ?? world.Add(new SavedCharacterSheet { PilotId = pilotId }).Entity).Apply(after);
            }
        }
        var saved = world.Filter<QueuedLevel>(q => q.PilotId == pilotId).OrderBy(q => q.Position).ToArray();
        List<TrainingEntry> queue = [];
        HashSet<int> changed = [];
        bool alphaBudgetStopped = false;
        foreach (var row in saved)
        {
            var entry = new TrainingEntry(new(row.TypeId, row.Level), row.StartPoints, row.EndPoints, row.StartTime, row.EndTime)
            {
                Periods = JsonSerializer.Deserialize<TrainingProgressPeriod[]>(row.PeriodsJson) ?? [],
            };
            if (entry.EndTime is long end && end <= now)
            {
                world.Set<PilotSkill>().Find(pilotId, row.TypeId)!.Points = row.EndPoints;
                changed.Add(row.TypeId);
                using CatalogDbContext catalog = new(database.CatalogPath);
                double rank = catalog.FittingAttributes(row.TypeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1);
                long threshold = TrainedSkill.PointsForLevel(rank, row.Level);
                if (row.EndPoints < threshold)
                {
                    // A reward shortened passive training to Alpha's total-SP cap,
                    // not to the requested level. Retain that level paused.
                    queue.Add(new(entry.Target, row.EndPoints, threshold, null, null));
                    alphaBudgetStopped = true;
                    Record(world, pilotId, end, row.TypeId, row.EndPoints, Cancelled);
                }
                else
                {
                    Record(world, pilotId, end, row.TypeId, row.EndPoints, Completed);
                }
                world.Remove(row);
            }
            else
            {
                queue.Add(entry);
            }
        }
        world.SaveChanges();
        var first = queue.FirstOrDefault();
        TrainingEntry? rebasedHead = null;
        if (alphaBudgetStopped)
        {
            targets = queue.Select(e => e.Target).ToArray();
            activate = false;
        }
        if (changeAttributes is not null)
        {
            bool wasActive = first?.StartTime is not null;
            if (wasActive)
            {
                rebasedHead = first;
                world.Set<PilotSkill>().Find(pilotId, first!.Target.TypeId)!.Points = first.PointsAt(now);
                changed.Add(first.Target.TypeId);
                world.SaveChanges();
            }
            // Materialize elapsed progress using the saved queue's old rate, then
            // change the physical body/implants and plan the remaining work once.
            changeAttributes();
            if (implantChange)
            {
                var revision =
                    world.Set<SavedImplantState>().Find(pilotId) ?? world.Add(new SavedImplantState { PilotId = pilotId }).Entity;
                revision.Revision = checked(revision.Revision + 1);
            }
            world.SaveChanges();
            targets = queue.Select(e => e.Target).ToArray();
            activate = wasActive;
            first = null;
        }
        FreePointPlan? pointPlan = null;
        if (points is not null)
        {
            bool wasActive = first?.StartTime is not null;
            if (wasActive)
            {
                rebasedHead = first;
            }
            if (wasActive)
            {
                world.Set<PilotSkill>().Find(pilotId, first!.Target.TypeId)!.Points = first.PointsAt(now);
                world.SaveChanges();
            }
            pointPlan = ChangePoints(world, pilotId, points, queue, changed, now, preview);
            if (preview)
            {
                return new(new(queue, []), false, pointPlan);
            }
            if (pointPlan.Allocations.Count > 0 || points.Credit > 0)
            {
                if (wasActive)
                {
                    changed.Add(first!.Target.TypeId);
                }
                var inputs = ReadInputs(world, pilotId, queue.Select(e => e.Target).ToArray(), now);
                targets = queue
                    .Select(e => e.Target)
                    .Where(t => t.Level > new TrainedSkill(t.TypeId, inputs.Skills[t.TypeId].Points, inputs.Skills[t.TypeId].Rank).Level)
                    .ToArray();
                activate = wasActive;
                first = null;
            }
        }
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
            queue = Plan(
                world,
                pilotId,
                targets,
                activate,
                keepHead ? first : null,
                now,
                points is not null || changeAttributes is not null,
                queue
            );
            activate = queue.FirstOrDefault()?.StartTime is not null;
            world.RemoveRange(world.Filter<QueuedLevel>(q => q.PilotId == pilotId).ToArray());
            world.SaveChanges();
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
                        PeriodsJson = JsonSerializer.Serialize(entry.Periods),
                    }
                );
            }
            if (activate && !keepHead && queue.Count > 0 && (rebasedHead is null || rebasedHead.Target != queue[0].Target))
            {
                Record(world, pilotId, now, queue[0].Target.TypeId, queue[0].StartPoints, Started);
            }
            if (rebasedHead is not null && queue is [{ StartTime: null }, ..])
            {
                long earned = world.Set<PilotSkill>().Find(pilotId, rebasedHead.Target.TypeId)!.Points;
                Record(world, pilotId, now, rebasedHead.Target.TypeId, earned, Cancelled);
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
        return new(new(queue, changed.ToArray()), remapped, pointPlan, world.Set<SavedFreeSkillPoints>().Find(pilotId)?.Revision ?? 0);
    }

    private List<TrainingEntry> Plan(
        WorldAction world,
        long pilotId,
        IReadOnlyList<TrainingTarget> targets,
        bool activate,
        TrainingEntry? running,
        long now,
        bool clampAlphaBudget = false,
        IReadOnlyList<TrainingEntry>? priorQueue = null
    )
    {
        if (targets.Count == 0)
        {
            return [];
        }
        var inputs = ReadInputs(world, pilotId, targets, now);
        bool partial =
            priorQueue?.Any(e => e.EndPoints < TrainedSkill.PointsForLevel(inputs.Skills[e.Target.TypeId].Rank, e.Target.Level)) == true;
        return TrainingRules.Plan(targets, inputs, activate, running, now, clampAlphaBudget || partial);
    }

    private TrainingInputs ReadInputs(WorldDbContext world, long pilotId, IReadOnlyList<TrainingTarget> targets, long now) =>
        ReadInputs(new SqlWorldAction(world), pilotId, targets, now);

    private TrainingInputs ReadInputs(WorldAction world, long pilotId, IReadOnlyList<TrainingTarget> targets, long now)
    {
        var points = world.Filter<PilotSkill>(s => s.PilotId == pilotId).ToDictionary(s => s.TypeId, s => s.Points);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        int race = pilot.RaceId;
        var alpha = catalog
            .Require(CatalogData.Client.Clonegrades, race.ToString(CultureInfo.InvariantCulture))
            .Skills!.Value.ToDictionary(s => s.TypeID!.Value, s => s.Level!.Value);
        var prerequisites = targets
            .Select(t => t.TypeId)
            .Distinct()
            .ToDictionary(type => type, type => catalog.Read(CatalogData.Client.Requiredskillsfortypes, (long)type));
        var implants = PilotImplantStore.ActiveImplants(world, pilotId).Select(implant => implant.Snapshot()).ToArray();
        var attributes = world.Set<SavedCharacterSheet>().Find(pilotId)?.Snapshot().Attributes ?? CharacterAttributes.Default;
        var basis = ImplantRules.Effective(attributes, implants).ToDogma();
        var boosters = world
            .Filter<SavedBooster>(b => b.PilotId == pilotId && b.ExpiresAt > now && b.Bonus > 0)
            .OrderBy(b => b.Slot)
            .ToArray();
        int bonus = boosters.Sum(b => b.Bonus);
        var inputs = points.ToDictionary(
            p => p.Key,
            p =>
            {
                var dogma = catalog.FittingAttributes(p.Key);
                var required = prerequisites.GetValueOrDefault(p.Key);
                var primary = (DogmaAttributeId)dogma.GetValueOrDefault(DogmaAttributeId.PrimaryAttribute);
                var secondary = (DogmaAttributeId)dogma.GetValueOrDefault(DogmaAttributeId.SecondaryAttribute);
                return new TrainingSkill(
                    p.Value,
                    dogma.GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1),
                    basis.GetValueOrDefault(primary) + bonus,
                    basis.GetValueOrDefault(secondary) + bonus,
                    checked((int)alpha.GetValueOrDefault(p.Key)),
                    required is null
                        ? []
                        : required
                            .Select(r => new TrainingTarget(int.Parse(r.Key, CultureInfo.InvariantCulture), checked((int)r.Value)))
                            .ToArray()
                )
                {
                    RateChanges = boosters
                        .Select(b => b.ExpiresAt)
                        .Distinct()
                        .Order()
                        .Select(time =>
                        {
                            int remaining = boosters.Where(b => b.ExpiresAt > time).Sum(b => b.Bonus);
                            return new TrainingRateChange(
                                time,
                                basis.GetValueOrDefault(primary) + remaining,
                                basis.GetValueOrDefault(secondary) + remaining
                            );
                        })
                        .ToArray(),
                };
            }
        );
        var grade = pilot.Account.CloneGrade;
        return new(inputs, grade, ReadFreePoints(world, pilotId));
    }

    private void Record(WorldDbContext world, long pilotId, long time, int typeId, long points, int eventId) =>
        Record(new SqlWorldAction(world), pilotId, time, typeId, points, eventId);

    private void Record(WorldAction world, long pilotId, long time, int typeId, long points, int eventId)
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
