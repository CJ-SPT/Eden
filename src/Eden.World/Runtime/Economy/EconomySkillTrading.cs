using System.Text.Json;
using Eden.Data.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedSkillExtraction(long Id, long PilotId, long ItemId, long Time, string AllocationsJson);

internal sealed record EconomySavedSkillInjection(
    long Id,
    long PilotId,
    long ItemId,
    int TypeId,
    int Quantity,
    long Points,
    long TotalBefore,
    long TotalAfter,
    int UsesSpent,
    long Time
);

internal sealed partial class ResidentEconomy
{
    private void RegisterSkillTrading()
    {
        Register(
            new EconomyTable<SavedSkillExtraction, EconomySavedSkillExtraction>(
                "WorldSkillExtraction",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.ItemId, row.Time, row.AllocationsJson),
                state => new SavedSkillExtraction
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    ItemId = state.ItemId,
                    Time = state.Time,
                    AllocationsJson = state.AllocationsJson,
                },
                ProjectSavedSkillExtraction,
                "Id,PilotId,ItemId,Time,AllocationsJson",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5)),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedSkillInjection, EconomySavedSkillInjection>(
                "WorldSkillInjection",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.PilotId,
                        row.ItemId,
                        row.TypeId,
                        row.Quantity,
                        row.Points,
                        row.TotalBefore,
                        row.TotalAfter,
                        row.UsesSpent,
                        row.Time
                    ),
                state => new SavedSkillInjection
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    ItemId = state.ItemId,
                    TypeId = state.TypeId,
                    Quantity = state.Quantity,
                    Points = state.Points,
                    TotalBefore = state.TotalBefore,
                    TotalAfter = state.TotalAfter,
                    UsesSpent = state.UsesSpent,
                    Time = state.Time,
                },
                ProjectSavedSkillInjection,
                "Id,PilotId,ItemId,TypeId,Quantity,Points,TotalBefore,TotalAfter,UsesSpent,Time",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt32(9),
                        reader.GetInt64(10)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectSavedSkillExtraction(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedSkillExtraction>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSkillExtraction WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSkillExtraction(Id,PilotId,ItemId,Time,AllocationsJson)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.PilotId'), json_extract(value,'$.ItemId'),
                    json_extract(value,'$.Time'), json_extract(value,'$.AllocationsJson')
                FROM json_each({values})
                """
            );
        }
    }

    private static void ProjectSavedSkillInjection(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedSkillInjection>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSkillInjection WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSkillInjection(Id,PilotId,ItemId,TypeId,Quantity,Points,TotalBefore,TotalAfter,UsesSpent,Time)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.PilotId'), json_extract(value,'$.ItemId'),
                    json_extract(value,'$.TypeId'), json_extract(value,'$.Quantity'), json_extract(value,'$.Points'),
                    json_extract(value,'$.TotalBefore'), json_extract(value,'$.TotalAfter'), json_extract(value,'$.UsesSpent'),
                    json_extract(value,'$.Time')
                FROM json_each({values})
                """
            );
        }
    }
}
