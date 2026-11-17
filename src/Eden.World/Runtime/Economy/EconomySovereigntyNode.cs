using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyNode(
    long Id,
    int SystemId,
    int SourceSystemId,
    long HubId,
    int CorporationId,
    int AllianceId,
    double X,
    double Y,
    double Z,
    double Value,
    long UpdatedAt,
    int? ScoringTeam,
    double Rate,
    bool Blocked
);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyNode() =>
        Register(
            new EconomyTable<SavedSovereigntyNode, EconomySovereigntyNode>(
                "WorldSovereigntyNode",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.SystemId,
                        row.SourceSystemId,
                        row.HubId,
                        row.CorporationId,
                        row.AllianceId,
                        row.X,
                        row.Y,
                        row.Z,
                        row.Value,
                        row.UpdatedAt,
                        row.ScoringTeam,
                        row.Rate,
                        row.Blocked
                    ),
                state => new SavedSovereigntyNode
                {
                    Id = state.Id,
                    SystemId = state.SystemId,
                    SourceSystemId = state.SourceSystemId,
                    HubId = state.HubId,
                    CorporationId = state.CorporationId,
                    AllianceId = state.AllianceId,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                    Value = state.Value,
                    UpdatedAt = state.UpdatedAt,
                    ScoringTeam = state.ScoringTeam,
                    Rate = state.Rate,
                    Blocked = state.Blocked,
                },
                ProjectSovereigntyNode,
                "Id,SystemId,SourceSystemId,HubId,CorporationId,AllianceId,X,Y,Z,Value,UpdatedAt,ScoringTeam,Rate,Blocked",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt64(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetDouble(7),
                        reader.GetDouble(8),
                        reader.GetDouble(9),
                        reader.GetDouble(10),
                        reader.GetInt64(11),
                        reader.IsDBNull(12) ? null : reader.GetInt32(12),
                        reader.GetDouble(13),
                        reader.GetBoolean(14)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("SystemId", row => row.SystemId),
                    new("SourceSystemId", row => row.SourceSystemId),
                    new("HubId", row => row.HubId),
                    new("AllianceId", row => row.AllianceId),
                ],
                primaryColumn: "Id"
            )
        );

    private static void ProjectSovereigntyNode(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySovereigntyNode>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyNode WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyNode(
                    Id,
                    SystemId,
                    SourceSystemId,
                    HubId,
                    CorporationId,
                    AllianceId,
                    X,
                    Y,
                    Z,
                    Value,
                    UpdatedAt,
                    ScoringTeam,
                    Rate,
                    Blocked)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.SourceSystemId'),
                    json_extract(value,'$.HubId'),
                    json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.X'),
                    json_extract(value,'$.Y'),
                    json_extract(value,'$.Z'),
                    json_extract(value,'$.Value'),
                    json_extract(value,'$.UpdatedAt'),
                    json_extract(value,'$.ScoringTeam'),
                    json_extract(value,'$.Rate'),
                    json_extract(value,'$.Blocked')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    SystemId=excluded.SystemId,
                    SourceSystemId=excluded.SourceSystemId,
                    HubId=excluded.HubId,
                    CorporationId=excluded.CorporationId,
                    AllianceId=excluded.AllianceId,
                    X=excluded.X,
                    Y=excluded.Y,
                    Z=excluded.Z,
                    Value=excluded.Value,
                    UpdatedAt=excluded.UpdatedAt,
                    ScoringTeam=excluded.ScoringTeam,
                    Rate=excluded.Rate,
                    Blocked=excluded.Blocked
                """
            );
        }
    }
}
