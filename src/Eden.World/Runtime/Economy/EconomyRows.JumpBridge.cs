using System.Text.Json;
using Eden.Data.World.Structures;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record JumpBridgeRow(long StructureId, int DestinationSystemId, double Capacitor, long ChargedAt);

    private void RegisterJumpBridges()
    {
        Register(
            new EconomyTable<SavedJumpBridge, JumpBridgeRow>(
                "WorldJumpBridge",
                row => new(row.StructureId),
                row => new(row.StructureId, row.DestinationSystemId, row.Capacitor, row.ChargedAt),
                row =>
                    new()
                    {
                        StructureId = row.StructureId,
                        DestinationSystemId = row.DestinationSystemId,
                        Capacitor = row.Capacitor,
                        ChargedAt = row.ChargedAt,
                    },
                ProjectJumpBridges,
                "StructureId,DestinationSystemId,Capacitor,ChargedAt",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetDouble(3), reader.GetInt64(4)),
                row => new(row.StructureId),
                primaryColumn: "StructureId",
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectJumpBridges(WorldDbContext world, IReadOnlyList<EconomyChange<JumpBridgeRow>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            string values = JsonSerializer.Serialize(batch.Select(change => change.Value).OfType<JumpBridgeRow>());
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldJumpBridge WHERE StructureId IN (SELECT value FROM json_each({removed}));
                INSERT INTO WorldJumpBridge(StructureId,DestinationSystemId,Capacitor,ChargedAt)
                SELECT value->>'StructureId',value->>'DestinationSystemId',value->>'Capacitor',value->>'ChargedAt'
                FROM json_each({values}) WHERE 1
                ON CONFLICT(StructureId) DO UPDATE SET Capacitor=excluded.Capacitor,ChargedAt=excluded.ChargedAt;
                """
            );
        }
    }
}
