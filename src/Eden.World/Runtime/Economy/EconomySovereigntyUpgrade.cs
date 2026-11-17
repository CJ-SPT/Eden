using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyUpgrade(
    long Id,
    long HubId,
    int SystemId,
    int TypeId,
    int Priority,
    bool ConfiguredOnline,
    SovereigntyUpgradePower PowerState,
    bool StartupPaid,
    long? RunningSince
);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyUpgrade() =>
        Register(
            new EconomyTable<SavedSovereigntyUpgrade, EconomySovereigntyUpgrade>(
                "WorldSovereigntyUpgrade",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.HubId,
                        row.SystemId,
                        row.TypeId,
                        row.Priority,
                        row.ConfiguredOnline,
                        row.PowerState,
                        row.StartupPaid,
                        row.RunningSince
                    ),
                state => new SavedSovereigntyUpgrade
                {
                    Id = state.Id,
                    HubId = state.HubId,
                    SystemId = state.SystemId,
                    TypeId = state.TypeId,
                    Priority = state.Priority,
                    ConfiguredOnline = state.ConfiguredOnline,
                    PowerState = state.PowerState,
                    StartupPaid = state.StartupPaid,
                    RunningSince = state.RunningSince,
                },
                ProjectSovereigntyUpgrade,
                "Id,HubId,SystemId,TypeId,Priority,ConfiguredOnline,PowerState,StartupPaid,RunningSince",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetBoolean(6),
                        (SovereigntyUpgradePower)reader.GetInt32(7),
                        reader.GetBoolean(8),
                        reader.IsDBNull(9) ? null : reader.GetInt64(9)
                    ),
                state => new(state.Id),
                indexes: [new("HubId", row => row.HubId), new("SystemId", row => row.SystemId)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectSovereigntyUpgrade(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySovereigntyUpgrade>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyUpgrade WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyUpgrade(Id,HubId,SystemId,TypeId,Priority,ConfiguredOnline,PowerState,StartupPaid,RunningSince)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.HubId'),
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.TypeId'),
                    json_extract(value,'$.Priority'),
                    json_extract(value,'$.ConfiguredOnline'),
                    json_extract(value,'$.PowerState'),
                    json_extract(value,'$.StartupPaid'),
                    json_extract(value,'$.RunningSince')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    HubId=excluded.HubId,
                    SystemId=excluded.SystemId,
                    TypeId=excluded.TypeId,
                    Priority=excluded.Priority,
                    ConfiguredOnline=excluded.ConfiguredOnline,
                    PowerState=excluded.PowerState,
                    StartupPaid=excluded.StartupPaid,
                    RunningSince=excluded.RunningSince
                """
            );
        }
    }
}
