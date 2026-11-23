using System.Text.Json;
using Eden.Data.World.Skills;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record EconomySkillPlanBook(long PilotId, SavedSkillPlanBook Book);

    private void RegisterPersonalSkillPlans() =>
        Register(
            new EconomyTable<SavedPersonalSkillPlans, EconomySkillPlanBook>(
                "WorldPersonalSkillPlans",
                row => new(row.PilotId),
                row => new(row.PilotId, row.Book),
                state => new SavedPersonalSkillPlans { PilotId = state.PilotId, Book = state.Book },
                ProjectPersonalSkillPlans,
                "PilotId,Payload",
                reader => new(reader.GetInt64(1), PersonalSkillPlanSchema.ReadBook(reader.GetString(2))),
                state => new(state.PilotId),
                primaryColumn: "PilotId"
            )
        );

    private static void ProjectPersonalSkillPlans(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySkillPlanBook>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldPersonalSkillPlans WHERE PilotId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPersonalSkillPlans(PilotId,Payload)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.Book')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET Payload=excluded.Payload
                """
            );
        }
    }
}
