using System.Data.Common;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Eden.World.Runtime;

// Legacy operations synchronize at their transaction boundary. Read-only queries only
// need the projection when they actually reference mutable combat tables.
internal sealed class CombatPersistenceBridge(string path) : DbCommandInterceptor
{
    private void Before(DbCommand command, CommandEventData data)
    {
        if (command.Connection is { } connection)
        {
            WorldConnections.FinalizeCommitted(connection);
        }
        if (data.Context is WorldDbContext { IsResidentProjection: true })
        {
            return;
        }
        if (
            data.Context?.Database.CurrentTransaction is null
            && LiveWorld.Find(path) is { } economicOwner
            && economicOwner.Economy.ReferencesDirty(command.CommandText)
        )
        {
            economicOwner.Economy.Flush();
        }
        if (
            data.Context is not WorldDbContext { UsesResidentCapsuleerRoutines: true }
            && command.CommandText.Contains("WorldCapsuleer", StringComparison.Ordinal)
            && data.Context?.Database.CurrentTransaction is null
        )
        {
            LiveWorld.Find(path)?.CapsuleerRoutines.Flush();
        }
        if (data.Context is WorldDbContext { Participants: { } participants })
        {
            WorldConnections.HoldSimulation((WorldDbContext)data.Context);
            if (data.Context.Database.CurrentTransaction is null)
            {
                CombatTransactionBridge.Project(path, participants);
            }
            return;
        }
        // Directional reads overlay resident positions under the owner. They never read
        // mutable combat resources, and must not force projection for a name/membership query.
        if (
            data.Context is WorldDbContext { ReadsDirectionalPositions: true }
            && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }
        bool read = command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
        string[] residentTables =
        [
            "WorldItem",
            "WorldPosition",
            "WorldShip",
            "WorldNpc",
            "WorldCombatDamage",
            "WorldDrone",
            "WorldArmorRepair",
            "WorldWreck",
        ];
        bool timer =
            data.Context is not WorldDbContext { ReadsResidentNpcTimer: true }
            && new[] { "WorldCrimewatch", "WorldPilotHostility", "WorldEngagement", "WorldLogoff" }.Any(table =>
                command.CommandText.Contains(table, StringComparison.Ordinal)
            );
        if (read && !timer && !residentTables.Any(table => SqlTableReferences.Contains(command.CommandText, table)))
        {
            return;
        }
        if (data.Context?.Database.CurrentTransaction is null)
        {
            if (data.Context is WorldDbContext context)
            {
                WorldConnections.HoldSimulation(context);
            }
            LiveWorld.Find(path)?.Economy.Flush();
            LiveWorld.Find(path)?.BeforeSystemsSql(command.CommandText, data.Context is WorldDbContext { ReadsResidentNpcTimer: true });
            LiveWorld.Find(path)?.BeforeSharedSql(command.CommandText, data.Context is WorldDbContext { ReadsResidentNpcTimer: true });
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
    internal static void Project(string path, TransactionParticipants? participants)
    {
        using var phase = SessionPerformance.Phase(participants is null ? "projection.fallback" : "projection.participants");
        var live = LiveWorld.Find(path);
        live?.Economy.Flush();
        live?.ProjectSystems(participants);
        live?.ProjectShared(participants);
    }

    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result
    )
    {
        WorldConnections.FinalizeCommitted(connection);
        if (eventData.Context is not WorldDbContext { IsResidentProjection: true })
        {
            if (eventData.Context is WorldDbContext context)
            {
                WorldConnections.HoldSimulation(context);
            }
            if (eventData.Context is not WorldDbContext { UsesResidentCapsuleerRoutines: true })
            {
                LiveWorld.Find(path)?.CapsuleerRoutines.Flush((eventData.Context as WorldDbContext)?.Participants);
            }
            LiveWorld.Find(path)?.Economy.Flush();
            Project(path, (eventData.Context as WorldDbContext)?.Participants);
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
        if (eventData.Context is not WorldDbContext { IsResidentProjection: true })
        {
            if (eventData.Context is WorldDbContext context)
            {
                WorldConnections.HoldSimulation(context);
            }
            if (eventData.Context is not WorldDbContext { UsesResidentCapsuleerRoutines: true })
            {
                LiveWorld.Find(path)?.CapsuleerRoutines.Flush((eventData.Context as WorldDbContext)?.Participants);
            }
            LiveWorld.Find(path)?.Economy.Flush();
            Project(path, (eventData.Context as WorldDbContext)?.Participants);
        }
        return ValueTask.FromResult(result);
    }
}
