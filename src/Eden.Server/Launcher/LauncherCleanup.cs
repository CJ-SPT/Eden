namespace Eden.Server.Launcher;

internal sealed record LauncherCleanup(int Tickets, int RefreshTokens, int Sessions)
{
    internal int Total => Tickets + RefreshTokens + Sessions;
}

internal sealed partial class LauncherStore
{
    internal const int CleanupBatchSize = 256;
    private const int MaximumCleanupBatches = 8;

    // Both branches use (Revoked,Expires). Limit candidate families before looking up children.
    private const string TerminalFamilies = """
        WITH terminal AS (
            SELECT Id FROM (SELECT Id FROM LauncherSession WHERE Revoked=0 AND Expires<=$now ORDER BY Expires LIMIT $batch)
            UNION ALL
            SELECT Id FROM (SELECT Id FROM LauncherSession WHERE Revoked=1 ORDER BY Expires LIMIT $batch)
            LIMIT $batch
        )
        """;

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

    internal LauncherCleanup CleanupExpired()
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        long now = Now;
        int expired = Execute(
            db,
            tx,
            "DELETE FROM LauncherTicket WHERE Hash IN (SELECT Hash FROM LauncherTicket WHERE Expires<=$now ORDER BY Expires LIMIT $batch)",
            ("$now", now),
            ("$batch", CleanupBatchSize)
        );
        int tickets = Execute(
            db,
            tx,
            TerminalFamilies
                + """
                DELETE FROM LauncherTicket WHERE Hash IN (
                    SELECT Hash FROM LauncherTicket WHERE SessionId IN (SELECT Id FROM terminal) LIMIT $batch
                )
                """,
            ("$now", now),
            ("$batch", CleanupBatchSize)
        );
        int refresh = Execute(
            db,
            tx,
            TerminalFamilies
                + """
                DELETE FROM LauncherRefresh WHERE Hash IN (
                    SELECT Hash FROM LauncherRefresh WHERE SessionId IN (SELECT Id FROM terminal) LIMIT $batch
                )
                """,
            ("$now", now),
            ("$batch", CleanupBatchSize)
        );
        int sessions = Execute(
            db,
            tx,
            TerminalFamilies
                + """
                DELETE FROM LauncherSession WHERE Id IN (SELECT Id FROM terminal)
                    AND NOT EXISTS (SELECT 1 FROM LauncherTicket WHERE SessionId=LauncherSession.Id)
                    AND NOT EXISTS (SELECT 1 FROM LauncherRefresh WHERE SessionId=LauncherSession.Id)
                """,
            ("$now", now),
            ("$batch", CleanupBatchSize)
        );
        tx.Commit();
        return new(expired + tickets, refresh, sessions);
    }
}
