using Eden.Protocol.PilotSessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Eden.Server.Persistence;

internal static class DatabasePerformance
{
    internal static DbContextOptionsBuilder TracePerformance(this DbContextOptionsBuilder options) =>
        options.LogTo(
            (id, _) =>
                SessionPerformance.IsActive
                && (
                    id == RelationalEventId.CommandExecuting
                    || id == RelationalEventId.ConnectionOpened
                    || id == RelationalEventId.CommandExecuted
                    || id == RelationalEventId.TransactionCommitted
                    || id == RelationalEventId.TransactionStarted
                ),
            Record
        );

    private static void Record(EventData data)
    {
        if (data is CommandExecutedEventData executed)
        {
            SessionPerformance.DatabaseElapsed(executed.Duration);
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
            SessionPerformance.DatabaseCommand(command.Command.CommandText);
        }
        else if (data is ConnectionEndEventData)
        {
            SessionPerformance.ConnectionOpened();
        }
    }
}
