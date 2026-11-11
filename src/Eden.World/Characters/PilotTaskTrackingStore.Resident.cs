using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

internal sealed partial class PilotTaskTrackingStore
{
    private static void CheckPilot(WorldAction world, long pilotId)
    {
        if (!world.Pilots.Any(pilot => pilot.Id == pilotId))
        {
            throw new InvalidDataException("Unknown pilot.");
        }
    }

    private static IReadOnlyList<TaskCounter> Counters(WorldAction world, long pilotId)
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
        WorldAction world,
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

    internal static void Record(WorldAction world, CatalogDbContext catalog, long pilotId, long now, string name, long count = 1)
    {
        var saved =
            world.Set<SavedTaskCounter>().Find(pilotId, name) ?? world.Add(new SavedTaskCounter { PilotId = pilotId, Name = name }).Entity;
        saved.Count = TaskTrackingRules.Increment(saved.Count, count);
        Complete(world, pilotId, now, Definitions(catalog), Counters(world, pilotId));
    }
}
