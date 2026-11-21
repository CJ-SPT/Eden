using System.Text.Json;
using Eden.Data.World.Structures;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyPilotTether(long ShipId, long PilotId, long StructureId, long EngagedAt, long RepairAt, bool RepairCompleted);

internal sealed partial class ResidentEconomy
{
    private void RegisterPilotTether() =>
        Register(
            new EconomyTable<SavedPilotTether, EconomyPilotTether>(
                "WorldPilotTether",
                row => new(row.ShipId),
                row => new(row.ShipId, row.PilotId, row.StructureId, row.EngagedAt, row.RepairAt, row.RepairCompleted),
                state => new SavedPilotTether
                {
                    ShipId = state.ShipId,
                    PilotId = state.PilotId,
                    StructureId = state.StructureId,
                    EngagedAt = state.EngagedAt,
                    RepairAt = state.RepairAt,
                    RepairCompleted = state.RepairCompleted,
                },
                ProjectPilotTether,
                "ShipId,PilotId,StructureId,EngagedAt,RepairAt,RepairCompleted",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetBoolean(6)
                    ),
                state => new(state.ShipId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "ShipId"
            )
        );

    private static void ProjectPilotTether(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyPilotTether>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldPilotTether WHERE ShipId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPilotTether(ShipId,PilotId,StructureId,EngagedAt,RepairAt,RepairCompleted)
                SELECT json_extract(value,'$.ShipId'),json_extract(value,'$.PilotId'),json_extract(value,'$.StructureId'),
                    json_extract(value,'$.EngagedAt'),json_extract(value,'$.RepairAt'),json_extract(value,'$.RepairCompleted')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(ShipId) DO UPDATE SET PilotId=excluded.PilotId,StructureId=excluded.StructureId,
                    EngagedAt=excluded.EngagedAt,RepairAt=excluded.RepairAt,RepairCompleted=excluded.RepairCompleted
                """
            );
        }
    }
}
