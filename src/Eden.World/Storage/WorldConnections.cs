using Eden.Gameplay.Thermal;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage;

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

    internal static void RequireDurableClose(System.Data.Common.DbConnection connection)
    {
        if (connection is LiveConnection live)
        {
            live.DurableOnClose = true;
        }
    }

    internal static void FinalizeCommitted(System.Data.Common.DbConnection connection)
    {
        if (connection is LiveConnection live)
        {
            live.FinalizeCommit();
        }
    }

    internal static void SynchronizeCommitted(WorldDbContext context)
    {
        if (context.Database.GetDbConnection() is LiveConnection connection)
        {
            connection.Synchronize(context);
        }
    }

    internal static ModuleCondition? ModuleCondition(WorldDbContext context, long pilot, long ship, long module) =>
        context.Database.GetDbConnection() is LiveConnection connection ? connection.Condition(pilot, ship, module) : null;

    internal static bool TryNaniteRepairing(WorldDbContext context, long module, long now, out bool repairing)
    {
        if (context.Database.GetDbConnection() is LiveConnection connection)
        {
            return connection.TryNaniteRepairing(module, now, out repairing);
        }
        repairing = false;
        return false;
    }

    // Publish only after the owning transaction commits, while its connection
    // still holds the world owner. Failed payments must never create live heat.
    internal static void PublishHeat(WorldDbContext context, long pilot, long ship, ShipHeatSnapshot state)
    {
        if (context.Database.GetDbConnection() is LiveConnection connection)
        {
            connection.PublishHeat(pilot, ship, state);
        }
    }

    // Existing persistence operations use synchronous SQLite scopes. The lease serializes
    // complete connection/transaction lifetimes, avoiding shared-cache lock retries.
    private sealed class LiveConnection(LiveWorld world) : SqliteConnection(world.ConnectionString)
    {
        private IDisposable? _lease;
        private readonly HashSet<CombatRowChange> _changes = [];
        private readonly HashSet<CombatRowChange> _pendingChanges = [];
        private readonly HashSet<CombatRowChange> _committingChanges = [];

        internal void FinalizeCommit()
        {
            _changes.UnionWith(_committingChanges);
            _committingChanges.Clear();
        }

        private bool _itemMembershipWrite;
        private bool _positionMembershipWrite;
        private bool _schemaWrite;
        internal bool DurableOnClose { get; set; }

        internal ModuleCondition? Condition(long pilot, long ship, long module) =>
            (
                _changes.Contains(new("WorldModuleCondition", module))
                || _pendingChanges.Contains(new("WorldModuleCondition", module))
                || _committingChanges.Contains(new("WorldModuleCondition", module))
            )
                ? null
                : world.Heat.Condition(pilot, ship, module);

        internal bool TryNaniteRepairing(long module, long now, out bool repairing) =>
            world.Nanites.TryRepairing(module, now, out repairing);

        internal void PublishHeat(long pilot, long ship, ShipHeatSnapshot state)
        {
            world.Heat.PublishCommitted(pilot, ship, state);
            _changes.Remove(new("WorldShipHeat", ship));
            _committingChanges.Remove(new("WorldShipHeat", ship));
        }

        internal void Synchronize(WorldDbContext context)
        {
            FinalizeCommit();
            if (world.Combat is { } runtime)
            {
                foreach (var change in runtime.Synchronize(context, _changes))
                {
                    if (change.Table == "WorldPosition")
                    {
                        world.Reads?.GateWatcherPositionChanged(change.Id);
                    }
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
                if (world.Combat is not null || world.Reads is not null || world.TracksTimedModules)
                {
                    // The commit callback runs before SQLite completes COMMIT. Stage its
                    // rows until the next successful statement/close; a failed commit's
                    // rollback callback discards them. Never publish rolled-back writes.
                    SQLitePCL.raw.sqlite3_commit_hook(
                        Handle!,
                        _ =>
                        {
                            FinalizeCommit();
                            _committingChanges.UnionWith(_pendingChanges);
                            _pendingChanges.Clear();
                            return 0;
                        },
                        null
                    );
                    SQLitePCL.raw.sqlite3_rollback_hook(
                        Handle!,
                        _ =>
                        {
                            _pendingChanges.Clear();
                            _committingChanges.Clear();
                        },
                        null
                    );
                    SQLitePCL.raw.sqlite3_update_hook(
                        Handle!,
                        (SQLitePCL.strdelegate_update)(
                            (_, operation, _, table, id) =>
                            {
                                FinalizeCommit();
                                _pendingChanges.Add(new(table, id));
                                if (
                                    string.Equals(table, "WorldPosition", StringComparison.OrdinalIgnoreCase)
                                    && (operation != SQLitePCL.raw.SQLITE_UPDATE || _positionMembershipWrite)
                                )
                                {
                                    // Corporation location roles depend on docking membership,
                                    // while ordinary coordinates stay in resident motion state.
                                    _pendingChanges.Add(new("WorldPositionMembership", id));
                                }
                                if (
                                    string.Equals(table, "WorldItem", StringComparison.OrdinalIgnoreCase)
                                    && (operation != SQLitePCL.raw.SQLITE_UPDATE || _itemMembershipWrite)
                                )
                                {
                                    _pendingChanges.Add(new("WorldItemMembership", id));
                                }
                            }
                        ),
                        null
                    );
                    SQLitePCL.raw.sqlite3_set_authorizer(
                        Handle!,
                        (SQLitePCL.strdelegate_authorizer)(
                            (_, action, table, column, _, _) =>
                            {
                                if (
                                    action
                                    is SQLitePCL.raw.SQLITE_ALTER_TABLE
                                        or SQLitePCL.raw.SQLITE_CREATE_TABLE
                                        or SQLitePCL.raw.SQLITE_DROP_TABLE
                                        or SQLitePCL.raw.SQLITE_DROP_INDEX
                                        or SQLitePCL.raw.SQLITE_CREATE_TRIGGER
                                        or SQLitePCL.raw.SQLITE_DROP_TRIGGER
                                )
                                {
                                    _schemaWrite = true;
                                    _pendingChanges.Add(new("schema", 0));
                                }
                                if (
                                    action == SQLitePCL.raw.SQLITE_UPDATE
                                    && string.Equals(table, "WorldItem", StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(column, "Quantity", StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(column, "CrystalDamage", StringComparison.OrdinalIgnoreCase)
                                )
                                {
                                    _itemMembershipWrite = true;
                                }
                                if (
                                    action == SQLitePCL.raw.SQLITE_UPDATE
                                    && string.Equals(table, "WorldPosition", StringComparison.OrdinalIgnoreCase)
                                    && (
                                        string.Equals(column, "ShipId", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(column, "SystemId", StringComparison.OrdinalIgnoreCase)
                                    )
                                )
                                {
                                    _positionMembershipWrite = true;
                                }
                                // Row callbacks must also cover unqualified bulk DELETE operations.
                                // Schema writes must retain SQLite's normal DROP/ALTER behavior.
                                return
                                    action == SQLitePCL.raw.SQLITE_DELETE
                                    && !_schemaWrite
                                    && table?.StartsWith("World", StringComparison.OrdinalIgnoreCase) == true
                                    ? SQLitePCL.raw.SQLITE_IGNORE
                                    : SQLitePCL.raw.SQLITE_OK;
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
                        FinalizeCommit();
                        world.SqlClosed(_changes);
                        _pendingChanges.Clear();
                        _changes.Clear();
                        _itemMembershipWrite = false;
                        _positionMembershipWrite = false;
                        _schemaWrite = false;
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
