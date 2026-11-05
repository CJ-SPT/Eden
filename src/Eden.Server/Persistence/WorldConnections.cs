using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal static class WorldConnections
{
    internal static SqliteConnection FileConnection(string path) =>
        new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                ForeignKeys = true,
                Pooling = false,
            }.ToString()
        );

    internal static SqliteConnection Create(string path) =>
        LiveWorld.Find(path) is { } live ? new LiveConnection(live) : FileConnection(path);

    internal static void RequireDurableClose(SqliteConnection connection)
    {
        if (connection is LiveConnection live)
        {
            live.DurableOnClose = true;
        }
    }

    internal static void SynchronizeCommitted(WorldDbContext context)
    {
        if (context.Database.GetDbConnection() is LiveConnection connection)
        {
            connection.Synchronize(context);
        }
    }

    // Existing persistence operations use synchronous SQLite scopes. The lease serializes
    // complete connection/transaction lifetimes, avoiding shared-cache lock retries.
    private sealed class LiveConnection(LiveWorld world) : SqliteConnection(world.ConnectionString)
    {
        private IDisposable? _lease;
        private readonly HashSet<CombatRowChange> _changes = [];
        internal bool DurableOnClose { get; set; }

        internal void Synchronize(WorldDbContext context)
        {
            if (world.Combat is { } runtime)
            {
                foreach (var change in runtime.Synchronize(context, _changes))
                {
                    _changes.Remove(change);
                }
            }
        }

        public override void Open()
        {
            if (_lease is not null)
            {
                base.Open();
                return;
            }
            var lease = world.Enter();
            try
            {
                base.Open();
                using var command = CreateCommand();
                command.CommandText = "PRAGMA temp_store=MEMORY";
                command.ExecuteNonQuery();
                if (world.Combat is not null)
                {
                    SQLitePCL.raw.sqlite3_update_hook(
                        Handle!,
                        (SQLitePCL.strdelegate_update)((_, _, _, table, id) => _changes.Add(new(table, id))),
                        null
                    );
                    SQLitePCL.raw.sqlite3_set_authorizer(
                        Handle!,
                        (SQLitePCL.strdelegate_authorizer)(
                            (_, action, _, _, _, _) =>
                            {
                                // Row callbacks must also cover unqualified bulk DELETE operations.
                                return action == SQLitePCL.raw.SQLITE_DELETE ? SQLitePCL.raw.SQLITE_IGNORE : SQLitePCL.raw.SQLITE_OK;
                            }
                        ),
                        null
                    );
                }
                world.SqlOpened();
                _lease = lease;
            }
            catch
            {
                base.Close();
                lease.Dispose();
                throw;
            }
        }

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Open();
            return Task.CompletedTask;
        }

        public override void Close()
        {
            try
            {
                base.Close();
            }
            finally
            {
                if (Interlocked.Exchange(ref _lease, null) is { } lease)
                {
                    try
                    {
                        world.SqlClosed(_changes);
                        _changes.Clear();
                    }
                    finally
                    {
                        lease.Dispose();
                    }
                }
            }
            if (DurableOnClose)
            {
                DurableOnClose = false;
                world.FlushAsync().GetAwaiter().GetResult();
            }
        }

        public override Task CloseAsync()
        {
            Close();
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Close();
            }
            base.Dispose(disposing);
        }
    }
}
