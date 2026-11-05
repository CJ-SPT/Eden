using Eden.Protocol.PilotSessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Eden.Server.Persistence;

internal static class DatabasePerformance
{
    internal static DbContextOptionsBuilder TracePerformance(this DbContextOptionsBuilder options) =>
        options.LogTo(
            (id, _) =>
                SessionPerformance.IsActive && (id == RelationalEventId.CommandExecuting || id == RelationalEventId.ConnectionOpened),
            Record
        );

    private static void Record(EventData data)
    {
        if (data is CommandEventData command)
        {
            SessionPerformance.DatabaseCommand(command.Command.CommandText);
        }
        else if (data is ConnectionEndEventData)
        {
            SessionPerformance.ConnectionOpened();
        }
    }
}
