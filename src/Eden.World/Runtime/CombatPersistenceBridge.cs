using System.Data.Common;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Eden.World.Runtime;

// Legacy SQL synchronizes the resident inputs its commands reference. A transaction
// alone does not project the world; rollback keeps its captured resident state dirty.
internal sealed class CombatPersistenceBridge(string path, WorldDbContext? projectionOwner = null) : DbCommandInterceptor
{
    private static readonly string[] SimulationTables =
    [
        "WorldItem",
        "WorldPosition",
        "WorldShip",
        "WorldNpc",
        "WorldCombatDamage",
        "WorldNpcAggression",
        "WorldDrone",
        "WorldDroneCombatTriggers",
        "WorldArmorRepair",
        "WorldWeaponCooldown",
        "WorldShipEnergy",
        "WorldShipHealth",
        "WorldWreck",
        "WorldShipHeat",
        "WorldModuleCondition",
        "WorldNaniteRepair",
    ];

    private void Before(DbCommand command, CommandEventData data)
    {
        if (projectionOwner is not null)
        {
            LiveWorld.Find(path)?.Economy.BeforeSql(projectionOwner, command);
            return;
        }
        if (command.Connection is { } connection)
        {
            WorldConnections.FinalizeCommitted(connection);
        }
        if (data.Context is not WorldDbContext context || LiveWorld.Find(path) is not { } live)
        {
            return;
        }
        if (context.IsResidentProjection)
        {
            if (context.SynchronizesEconomicProjection)
            {
                live.Economy.BeforeSql(context, command);
            }
            return;
        }
        string sql = command.CommandText;
        bool schema = new[] { "CREATE ", "DROP ", "ALTER " }.Any(prefix =>
            sql.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        );
        live.Economy.BeforeSql(context, command);
        WorldDbContext? target = context.Database.CurrentTransaction is null ? null : context;
        if (schema || !context.UsesResidentCapsuleerRoutines && sql.Contains("WorldCapsuleer", StringComparison.Ordinal))
        {
            WorldConnections.HoldSimulation(context);
            live.CapsuleerRoutines.Flush(context.Participants, target);
        }
        if (context.ReadsDirectionalPositions && sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        bool timer =
            !context.ReadsResidentNpcTimer
            && new[] { "WorldCrimewatch", "WorldPilotHostility", "WorldEngagement", "WorldLogoff" }.Any(table =>
                SqlTableReferences.Contains(sql, table)
            );
        bool pilotWrite =
            !sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && SqlTableReferences.Contains(sql, "WorldPilot");
        if (schema || timer || pilotWrite || SimulationTables.Any(table => SqlTableReferences.Contains(sql, table)))
        {
            WorldConnections.HoldSimulation(context);
            CombatTransactionBridge.Project(path, context.Participants, target);
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result
    )
    {
        Before(command, eventData);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Before(command, eventData);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result
    )
    {
        Before(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default
    )
    {
        Before(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        Before(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default
    )
    {
        Before(command, eventData);
        return ValueTask.FromResult(result);
    }
}

internal sealed class CombatTransactionBridge(string path) : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData.Context is WorldDbContext context && (!context.IsResidentProjection || context.SynchronizesEconomicProjection))
        {
            context.EconomicProjection.CommitResidents();
            LiveWorld.Find(path)?.Economy.CompleteProjection(context);
        }
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        TransactionCommitted(transaction, eventData);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData.Context is WorldDbContext context)
        {
            context.EconomicProjection.Clear();
        }
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        TransactionRolledBack(transaction, eventData);
        return Task.CompletedTask;
    }

    internal static void Project(string path, TransactionParticipants? participants, WorldDbContext? target = null)
    {
        using var phase = SessionPerformance.Phase(participants is null ? "projection.fallback" : "projection.participants");
        var live = LiveWorld.Find(path);
        live?.ProjectSystems(participants, target);
        live?.ProjectShared(participants, target);
    }

    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result
    )
    {
        WorldConnections.FinalizeCommitted(connection);
        if (eventData.Context is not WorldDbContext { IsResidentProjection: true, SynchronizesEconomicProjection: false })
        {
            if (eventData.Context is WorldDbContext context)
            {
                if (!context.IsResidentProjection)
                {
                    // Burnout may require its own atomic write. Settle it before
                    // SQLite begins the caller's transaction, without flushing state.
                    LiveWorld.Find(path)?.SettleTransactionHeat(context.Participants);
                }
                context.EconomicProjection.Clear();
                WorldConnections.TrackEconomicProjection(connection, context.EconomicProjection);
            }
        }
        return result;
    }

    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default
    )
    {
        WorldConnections.FinalizeCommitted(connection);
        if (eventData.Context is not WorldDbContext { IsResidentProjection: true, SynchronizesEconomicProjection: false })
        {
            if (eventData.Context is WorldDbContext context)
            {
                if (!context.IsResidentProjection)
                {
                    LiveWorld.Find(path)?.SettleTransactionHeat(context.Participants);
                }
                context.EconomicProjection.Clear();
                WorldConnections.TrackEconomicProjection(connection, context.EconomicProjection);
            }
        }
        return ValueTask.FromResult(result);
    }
}
