using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntySkyhook(
    long Id,
    int SystemId,
    long PlanetId,
    int CorporationId,
    bool Active,
    long OnlineAt,
    long UpdatedAt,
    long Secured,
    long Surplus
);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntySkyhook() =>
        Register(
            new EconomyTable<SavedSovereigntySkyhook, EconomySovereigntySkyhook>(
                "WorldSovereigntySkyhook",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.SystemId,
                        row.PlanetId,
                        row.CorporationId,
                        row.Active,
                        row.OnlineAt,
                        row.UpdatedAt,
                        row.Secured,
                        row.Surplus
                    ),
                state => new SavedSovereigntySkyhook
                {
                    Id = state.Id,
                    SystemId = state.SystemId,
                    PlanetId = state.PlanetId,
                    CorporationId = state.CorporationId,
                    Active = state.Active,
                    OnlineAt = state.OnlineAt,
                    UpdatedAt = state.UpdatedAt,
                    Secured = state.Secured,
                    Surplus = state.Surplus,
                },
                ProjectSovereigntySkyhook,
                "Id,SystemId,PlanetId,CorporationId,Active,OnlineAt,UpdatedAt,Secured,Surplus",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt32(4),
                        reader.GetBoolean(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9)
                    ),
                state => new(state.Id),
                indexes: [new("SystemId", row => row.SystemId), new("PlanetId", row => row.PlanetId)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectSovereigntySkyhook(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySovereigntySkyhook>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntySkyhook WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntySkyhook(Id,SystemId,PlanetId,CorporationId,Active,OnlineAt,UpdatedAt,Secured,Surplus)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.PlanetId'),
                    json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.Active'),
                    json_extract(value,'$.OnlineAt'),
                    json_extract(value,'$.UpdatedAt'),
                    json_extract(value,'$.Secured'),
                    json_extract(value,'$.Surplus')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    SystemId=excluded.SystemId,
                    PlanetId=excluded.PlanetId,
                    CorporationId=excluded.CorporationId,
                    Active=excluded.Active,
                    OnlineAt=excluded.OnlineAt,
                    UpdatedAt=excluded.UpdatedAt,
                    Secured=excluded.Secured,
                    Surplus=excluded.Surplus
                """
            );
        }
    }
}
