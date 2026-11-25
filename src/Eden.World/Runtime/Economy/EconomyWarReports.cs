using System.Globalization;
using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record WarKillState(
        long Id,
        long WarId,
        int VictimOrganizationId,
        bool AttackerLoss,
        int GroupId,
        WarReportGroup? Group,
        long Time,
        decimal IskLost,
        decimal IskDestroyed,
        string Report
    );

    private void RegisterWarReports()
    {
        Register(
            new EconomyTable<SavedCorporationWarKill, WarKillState>(
                WarReportSchema.Table,
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.WarId,
                        row.VictimOrganizationId,
                        row.AttackerLoss,
                        row.GroupId,
                        row.Group,
                        row.Time,
                        row.IskLost,
                        row.IskDestroyed,
                        row.Report
                    ),
                row =>
                    new()
                    {
                        Id = row.Id,
                        WarId = row.WarId,
                        VictimOrganizationId = row.VictimOrganizationId,
                        AttackerLoss = row.AttackerLoss,
                        GroupId = row.GroupId,
                        Group = row.Group,
                        Time = row.Time,
                        IskLost = row.IskLost,
                        IskDestroyed = row.IskDestroyed,
                        Report = row.Report,
                    },
                ProjectWarKills,
                "Id,WarId,VictimOrganizationId,AttackerLoss,GroupId,\"Group\",Time,IskLost,IskDestroyed,Report",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetBoolean(4),
                        reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : (WarReportGroup)reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetDecimal(8),
                        reader.GetDecimal(9),
                        reader.GetString(10)
                    ),
                row => new(row.Id),
                indexes: [new(nameof(SavedCorporationWarKill.WarId), row => row.WarId)],
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectWarKills(WorldDbContext world, IReadOnlyList<EconomyChange<WarKillState>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationWarKill WHERE Id IN (SELECT value FROM json_each({deleted}))");
            string rows = JsonSerializer.Serialize(
                batch
                    .Select(change => change.Value)
                    .OfType<WarKillState>()
                    .Select(row => new
                    {
                        row.Id,
                        row.WarId,
                        row.VictimOrganizationId,
                        row.AttackerLoss,
                        row.GroupId,
                        row.Group,
                        row.Time,
                        row.Report,
                        IskLost = row.IskLost.ToString(CultureInfo.InvariantCulture),
                        IskDestroyed = row.IskDestroyed.ToString(CultureInfo.InvariantCulture),
                    })
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationWarKill(
                    Id,WarId,VictimOrganizationId,AttackerLoss,GroupId,"Group",Time,IskLost,IskDestroyed,Report)
                SELECT value->>'$.Id',value->>'$.WarId',value->>'$.VictimOrganizationId',value->>'$.AttackerLoss',
                    value->>'$.GroupId',value->>'$.Group',value->>'$.Time',value->>'$.IskLost',value->>'$.IskDestroyed',value->>'$.Report'
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO NOTHING
                """
            );
        }
    }
}
