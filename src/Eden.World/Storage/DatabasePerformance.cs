using Eden.Infrastructure.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Eden.World.Storage;

internal static class DatabasePerformance
{
    internal static DbContextOptionsBuilder TracePerformance(this DbContextOptionsBuilder options, string storage) =>
        options.LogTo(
            (id, _) =>
                (GameplaySqlGuard.IsActive && id == RelationalEventId.CommandExecuting)
                || SessionPerformance.IsActive
                    && (
                        id == RelationalEventId.CommandExecuting
                        || id == RelationalEventId.ConnectionOpened
                        || id == RelationalEventId.CommandExecuted
                        || id == RelationalEventId.TransactionCommitted
                        || id == RelationalEventId.TransactionStarted
                    ),
            data => Record(data, storage)
        );

    private static void Record(EventData data, string storage)
    {
        if (data is CommandExecutedEventData executed)
        {
            SessionPerformance.DatabaseElapsed(executed.Duration, command: executed.Command.CommandText);
        }
        else if (data is TransactionEndEventData transaction)
        {
            if (data.EventId == RelationalEventId.TransactionStarted)
            {
                SessionPerformance.TransactionStarted(transaction.Duration);
            }
            else
            {
                SessionPerformance.DatabaseElapsed(transaction.Duration, commit: true);
            }
        }
        else if (data is CommandEventData command)
        {
            GameplaySqlGuard.Check(storage);
            SessionPerformance.DatabaseCommand(command.Command.CommandText);
        }
        else if (data is ConnectionEndEventData)
        {
            SessionPerformance.ConnectionOpened();
        }
    }
}
