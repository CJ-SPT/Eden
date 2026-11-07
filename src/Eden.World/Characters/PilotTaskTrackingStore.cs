using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotTaskTrackingStore(WorldDatabase database, TimeProvider clock) : IPilotTaskTracking
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public TaskProgress Read(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        CheckPilot(world, pilotId);
        var counters = Counters(world, pilotId);
        return new(
            world
                .Set<SavedCompletedTask>()
                .Where(task => task.PilotId == pilotId)
                .OrderBy(task => task.Id)
                .Select(task => new CompletedTask(task.Id, task.Time))
                .ToArray(),
            counters.Where(counter => counter.Name.StartsWith("client_", StringComparison.Ordinal)).ToArray(),
            counters.Any(counter => counter.Name == TaskEvents.Warped && counter.Count > 0)
        );
    }

    public void Update(long pilotId, IReadOnlyList<CompletedTask> completed, IReadOnlyList<TaskCounter> counters)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        CheckPilot(world, pilotId);
        if (
            completed.Count > TaskTrackingRules.MaximumBatch
            || counters.Count > TaskTrackingRules.MaximumBatch
            || completed.Select(task => task.Id).Distinct().Count() != completed.Count
            || counters.Select(counter => counter.Name).Distinct(StringComparer.Ordinal).Count() != counters.Count
        )
        {
            throw new InvalidDataException("Invalid task snapshot.");
        }
        var definitions = Definitions(catalog);
        var clientEvents = catalog
            .Records(CatalogData.Client.Achievementevents)
            .Values.Select(record => record.Name)
            .Where(name => name?.StartsWith("client_", StringComparison.Ordinal) == true)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var counter in counters)
        {
            if (!clientEvents.Contains(counter.Name))
            {
                throw new InvalidDataException("Unknown client task counter.");
            }
            var saved = world.Set<SavedTaskCounter>().Find(pilotId, counter.Name);
            long count = TaskTrackingRules.MergeCount(saved?.Count ?? 0, counter.Count);
            saved ??= world.Add(new SavedTaskCounter { PilotId = pilotId, Name = counter.Name }).Entity;
            saved.Count = count;
        }
        var merged = Counters(world, pilotId);
        long now = Now;
        foreach (var task in completed)
        {
            var definition = definitions.SingleOrDefault(definition => definition.Id == task.Id);
            var saved = world.Set<SavedCompletedTask>().Find(pilotId, task.Id);
            if (
                definition is null
                || task.Time <= 0
                || task.Time > now
                || !definition.Client && saved is null
                || definition.Client && saved is null && !TaskTrackingRules.Satisfied(definition, merged)
            )
            {
                throw new InvalidDataException("Invalid or unearned task completion.");
            }
            if (definition.Client && saved is null)
            {
                world.Add(
                    new SavedCompletedTask
                    {
                        PilotId = pilotId,
                        Id = task.Id,
                        Time = task.Time,
                    }
                );
            }
        }
        Complete(world, pilotId, now, definitions, merged);
        world.SaveChanges();
        transaction.Commit();
    }

    private static void CheckPilot(WorldDbContext world, long pilotId)
    {
        if (!world.Pilots.Any(pilot => pilot.Id == pilotId))
        {
            throw new InvalidDataException("Unknown pilot.");
        }
    }

    private static IReadOnlyList<TaskDefinition> Definitions(CatalogDbContext catalog)
    {
        var events = catalog
            .Records(CatalogData.Client.Achievementevents)
            .ToDictionary(record => record.Key, record => record.Value.Name ?? "");
        return catalog
            .Records(CatalogData.Client.Achievements)
            .Select(record =>
            {
                var definition = record.Value;
                return new TaskDefinition(
                    record.Key,
                    definition.IsClientAchievement == true,
                    definition.IsEnabled == true,
                    definition
                        .SimpleConditions?.Values.Select(condition => new TaskCondition(
                            events.GetValueOrDefault(condition.Statistic ?? 0, ""),
                            condition.TargetValue ?? long.MaxValue
                        ))
                        .ToArray()
                        ?? []
                );
            })
            .ToArray();
    }

    private static IReadOnlyList<TaskCounter> Counters(WorldDbContext world, long pilotId)
    {
        // Query tracked rows as well as additions so one operation can record
        // multiple activities before its owning transaction saves anything.
        world.Set<SavedTaskCounter>().Where(counter => counter.PilotId == pilotId).Load();
        return world
            .Set<SavedTaskCounter>()
            .Local.Where(counter => counter.PilotId == pilotId)
            .Select(counter => new TaskCounter(counter.Name, counter.Count))
            .OrderBy(counter => counter.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void Complete(
        WorldDbContext world,
        long pilotId,
        long now,
        IReadOnlyList<TaskDefinition> definitions,
        IReadOnlyList<TaskCounter> counters
    )
    {
        foreach (var definition in definitions.Where(definition => TaskTrackingRules.Satisfied(definition, counters)))
        {
            if (world.Set<SavedCompletedTask>().Find(pilotId, definition.Id) is null)
            {
                world.Add(
                    new SavedCompletedTask
                    {
                        PilotId = pilotId,
                        Id = definition.Id,
                        Time = now,
                    }
                );
            }
        }
    }

    // Called inside the transaction that owns a successful gameplay event.
    internal static void Record(WorldDbContext world, CatalogDbContext catalog, long pilotId, long now, string name, long count = 1)
    {
        var saved =
            world.Set<SavedTaskCounter>().Find(pilotId, name) ?? world.Add(new SavedTaskCounter { PilotId = pilotId, Name = name }).Entity;
        saved.Count = TaskTrackingRules.Increment(saved.Count, count);
        Complete(world, pilotId, now, Definitions(catalog), Counters(world, pilotId));
    }
}
