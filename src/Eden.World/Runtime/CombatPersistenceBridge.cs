using System.Data.Common;
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
        if (data.Context?.Database.CurrentTransaction is null)
        {
            LiveCombatRuntime.Find(path)?.BeforeSql(command.CommandText, data.Context is WorldDbContext { ReadsResidentNpcTimer: true });
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
    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result
    )
    {
        LiveCombatRuntime.Find(path)?.Flush();
        return result;
    }

    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default
    )
    {
        LiveCombatRuntime.Find(path)?.Flush();
        return ValueTask.FromResult(result);
    }
}
