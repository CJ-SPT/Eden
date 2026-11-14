using Eden.Data.World.Launcher;

namespace Eden.Server.Launcher;

internal sealed record LauncherCleanup(int Tickets, int RefreshTokens, int Sessions)
{
    internal int Total => Tickets + RefreshTokens + Sessions;
}

internal sealed partial class LauncherStore
{
    internal const int CleanupBatchSize = 256;
    private const int MaximumCleanupBatches = 8;

    internal void Maintain()
    {
        for (int batch = 0; batch < MaximumCleanupBatches; batch++)
        {
            if (CleanupExpired().Total == 0)
            {
                break;
            }
        }
    }

    internal LauncherCleanup CleanupExpired() =>
        Write(world =>
        {
            long now = Now;
            var sessions = world
                .Filter<SavedLauncherSession>(row => !row.Revoked && row.Expires <= now)
                .OrderBy(row => row.Expires)
                .ThenBy(row => row.Id)
                .Take(CleanupBatchSize)
                .AsEnumerable()
                .Concat(
                    world
                        .Filter<SavedLauncherSession>(row => row.Revoked)
                        .OrderBy(row => row.Expires)
                        .ThenBy(row => row.Id)
                        .Take(CleanupBatchSize)
                )
                .Take(CleanupBatchSize)
                .ToArray();
            var expired = world
                .Filter<SavedLauncherTicket>(row => row.Expires <= now)
                .OrderBy(row => row.Expires)
                .ThenBy(row => row.Hash)
                .Take(CleanupBatchSize)
                .ToArray();
            var expiredKeys = expired.Select(row => row.Hash).ToHashSet(StringComparer.Ordinal);
            var tickets = sessions
                .SelectMany(session => world.Filter<SavedLauncherTicket>(row => row.SessionId == session.Id))
                .Where(row => !expiredKeys.Contains(row.Hash))
                .Take(CleanupBatchSize)
                .ToArray();
            var refreshes = sessions
                .SelectMany(session => world.Filter<SavedLauncherRefresh>(row => row.SessionId == session.Id))
                .Take(CleanupBatchSize)
                .ToArray();
            world.RemoveRange(expired);
            world.RemoveRange(tickets);
            world.RemoveRange(refreshes);
            world.SaveChanges();
            var removed = sessions
                .Where(session =>
                    !world.Filter<SavedLauncherRefresh>(row => row.SessionId == session.Id).Any()
                    && !world.Filter<SavedLauncherTicket>(row => row.SessionId == session.Id).Any()
                )
                .ToArray();
            world.RemoveRange(removed);
            return new LauncherCleanup(expired.Length + tickets.Length, refreshes.Length, removed.Length);
        });
}
