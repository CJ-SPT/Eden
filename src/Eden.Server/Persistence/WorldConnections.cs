using Microsoft.Data.Sqlite;

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

    // Existing persistence operations use synchronous SQLite scopes. The lease serializes
    // complete connection/transaction lifetimes, avoiding shared-cache lock retries.
    private sealed class LiveConnection(LiveWorld world) : SqliteConnection(world.ConnectionString)
    {
        private IDisposable? _lease;
        internal bool DurableOnClose { get; set; }

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
                Interlocked.Exchange(ref _lease, null)?.Dispose();
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
