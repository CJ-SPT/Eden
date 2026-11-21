using System.Text.Json;
using Eden.Data.World.Flight;
using Eden.Gameplay.Flight;
using Eden.World.Flight;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private void RegisterJumpPortals()
    {
        Register(
            new EconomyTable<SavedJumpPortal, JumpPortal>(
                "WorldJumpPortal",
                row => new(row.PilotId),
                PilotCapitalTravelStore.PortalContact,
                row => new SavedJumpPortal
                {
                    PilotId = row.PilotId,
                    ShipId = row.ShipId,
                    ModuleId = row.ModuleId,
                    FleetId = row.FleetId,
                    Origin = row.Origin,
                    Destination = row.Target.SystemId,
                    Target = row.Target.Target,
                    EmitterId = row.Target.EmitterId,
                    BeaconId = row.Target.BeaconId,
                    StartedAt = row.StartedAt,
                    EndsAt = row.EndsAt,
                },
                ProjectJumpPortals,
                "PilotId,ShipId,ModuleId,FleetId,Origin,Destination,Target,EmitterId,BeaconId,StartedAt,EndsAt",
                reader => new JumpPortal(
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt32(5),
                    new(reader.GetInt64(8), reader.GetInt64(9), reader.GetInt32(6), (CapitalJumpTarget)reader.GetInt32(7)),
                    reader.GetInt64(10),
                    reader.GetInt64(11)
                ),
                row => new(row.PilotId),
                indexes: [new(nameof(SavedJumpPortal.FleetId), row => row.FleetId), new(nameof(SavedJumpPortal.ShipId), row => row.ShipId)],
                changed: (_, row) => ObserveCynoDeadline(row?.EndsAt),
                primaryColumn: "PilotId",
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectJumpPortals(WorldDbContext world, IReadOnlyList<EconomyChange<JumpPortal>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            string values = JsonSerializer.Serialize(batch.Select(change => change.Value).OfType<JumpPortal>());
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldJumpPortal WHERE PilotId IN (SELECT value FROM json_each({removed}));
                INSERT INTO WorldJumpPortal(PilotId,ShipId,ModuleId,FleetId,Origin,Destination,Target,EmitterId,BeaconId,StartedAt,EndsAt)
                SELECT value->>'PilotId',value->>'ShipId',value->>'ModuleId',value->>'FleetId',value->>'Origin',
                    value->>'$.Target.SystemId',value->>'$.Target.Target',value->>'$.Target.EmitterId',value->>'$.Target.BeaconId',
                    value->>'StartedAt',value->>'EndsAt'
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET ShipId=excluded.ShipId,ModuleId=excluded.ModuleId,FleetId=excluded.FleetId,
                    Origin=excluded.Origin,Destination=excluded.Destination,Target=excluded.Target,EmitterId=excluded.EmitterId,
                    BeaconId=excluded.BeaconId,StartedAt=excluded.StartedAt,EndsAt=excluded.EndsAt;
                """
            );
        }
    }
}
