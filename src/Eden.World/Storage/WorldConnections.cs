using Eden.Gameplay.Thermal;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage;

internal static partial class WorldConnections
{
    internal static IDisposable? PreparingInstitutional(WorldDbContext context, int station) =>
        context.Database.GetDbConnection() is LiveConnection connection ? connection.PreparingInstitutional(station) : null;

    internal static bool PreparedInstitutional(WorldDbContext context, InstitutionalPreparation prepared) =>
        !context
            .ChangeTracker.Entries<Eden.Data.World.Market.NpcMarketOrder>()
            .Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
        && context.Database.CurrentTransaction is { } transaction
        && context.Database.GetDbConnection() is LiveConnection connection
        && connection.PreparedInstitutional(prepared, transaction.TransactionId);

    internal static void RememberInstitutional(WorldDbContext context, InstitutionalPreparation prepared)
    {
        if (context.Database.CurrentTransaction is { } transaction && context.Database.GetDbConnection() is LiveConnection connection)
        {
            connection.RememberInstitutional(prepared, transaction.TransactionId);
        }
    }

    internal static void CompleteInstitutional(WorldDbContext context, Guid transaction, bool committed)
    {
        if (context.Database.GetDbConnection() is LiveConnection connection)
        {
            connection.CompleteInstitutional(transaction, committed);
        }
    }

    internal static bool HasChanges(WorldDbContext context, params string[] tables) =>
        context
            .ChangeTracker.Entries()
            .Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && tables.Contains(e.Metadata.GetTableName(), StringComparer.Ordinal)
            )
        || context.Database.GetDbConnection() is LiveConnection connection && connection.HasChanges(tables);

    internal static SqliteConnection FileConnection(string path) =>
        new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                ForeignKeys = true,
                Pooling = false,
            }.ToString()
        );

    internal static SqliteConnection Create(
        string path,
        TransactionParticipants? participants = null,
        bool projection = false,
        IReadOnlySet<int>? committedSystems = null
    ) => LiveWorld.Find(path) is { } live ? new LiveConnection(live, participants, projection, committedSystems) : FileConnection(path);

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

    internal static void HoldSimulation(WorldDbContext context)
    {
        if (context.Database.GetDbConnection() is LiveConnection connection)
        {
            connection.HoldSimulation(context.Participants);
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
    private sealed class LiveConnection(
        LiveWorld world,
        TransactionParticipants? participants,
        bool projection,
        IReadOnlySet<int>? committedSystems
    ) : SqliteConnection(world.ConnectionString)
    {
        private readonly int? _projectionSystem = projection ? world.CurrentSystem : null;
        private IDisposable? _lease;
        private IDisposable? _simulation;

        internal void HoldSimulation(TransactionParticipants? participants) => _simulation ??= world.EnterTransactionSystems(participants);

        private readonly HashSet<CombatRowChange> _changes = [];
        private readonly HashSet<CombatRowChange> _pendingChanges = [];
        private readonly HashSet<CombatRowChange> _committingChanges = [];

        private sealed record PendingInstitutional(Guid Transaction, InstitutionalPreparation Preparation);

        private readonly Dictionary<int, PendingInstitutional> _pendingInstitutional = [];
        private readonly Dictionary<int, InstitutionalPreparation> _committedInstitutional = [];

        private int? _preparingStation;

        internal IDisposable PreparingInstitutional(int station)
        {
            var previous = _preparingStation;
            _preparingStation = station;
            return new InstitutionalScope(this, previous);
        }

        private sealed class InstitutionalScope(LiveConnection connection, int? previous) : IDisposable
        {
            public void Dispose() => connection._preparingStation = previous;
        }

        internal void RememberInstitutional(InstitutionalPreparation prepared, Guid transaction) =>
            _pendingInstitutional[prepared.StationId] = new(transaction, prepared);

        internal bool PreparedInstitutional(InstitutionalPreparation prepared, Guid transaction) =>
            _pendingInstitutional.GetValueOrDefault(prepared.StationId) == new PendingInstitutional(transaction, prepared);

        internal void CompleteInstitutional(Guid transaction, bool committed)
        {
            foreach (var pair in _pendingInstitutional.Where(p => p.Value.Transaction == transaction).ToArray())
            {
                if (committed)
                {
                    _committedInstitutional[pair.Key] = pair.Value.Preparation;
                }
                _pendingInstitutional.Remove(pair.Key);
            }
        }

        internal bool HasChanges(string[] tables) =>
            _changes
                .Concat(_pendingChanges)
                .Concat(_committingChanges)
                .Any(change => change.Table == "schema" || tables.Contains(change.Table, StringComparer.Ordinal));

        internal void FinalizeCommit()
        {
            _changes.UnionWith(_committingChanges);
            _committingChanges.Clear();
        }

        private bool _itemMembershipWrite;
        private bool _positionMembershipWrite;
        private bool _schemaWrite;
        private bool _marketDefinitionWrite;
        private bool _marketMembershipWrite;
        internal bool DurableOnClose { get; set; }

        internal ModuleCondition? Condition(long pilot, long ship, long module) =>
            (
                _changes.Contains(new("WorldModuleCondition", module))
                || _pendingChanges.Contains(new("WorldModuleCondition", module))
                || _committingChanges.Contains(new("WorldModuleCondition", module))
            )
                ? null
                : world.HeatForKnownShip(ship).Condition(pilot, ship, module);

        internal bool TryNaniteRepairing(long module, long now, out bool repairing) =>
            world.Nanites.TryRepairing(module, now, out repairing);

        internal void PublishHeat(long pilot, long ship, ShipHeatSnapshot state)
        {
            world.PublishHeat(pilot, ship, state);
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
            // Internal projection/membership reads already define their resident boundary.
            // A cold read must not request every unrelated system during the SQL handoff.
            var lease = world.EnterSql(projection && participants is null ? TransactionParticipants.None : participants);
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
                            _pendingInstitutional.Clear();
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
                                    (table is "WorldMarketOrder" or "WorldPersonalOrder")
                                    && (operation != SQLitePCL.raw.SQLITE_UPDATE || _marketDefinitionWrite)
                                )
                                {
                                    _pendingChanges.Add(new("WorldMarketDefinition", id));
                                    if (_preparingStation is { } station)
                                    {
                                        _pendingInstitutional.Remove(station);
                                        _committedInstitutional.Remove(station);
                                    }
                                    else
                                    {
                                        _pendingInstitutional.Clear();
                                        _committedInstitutional.Clear();
                                    }
                                }
                                if ((table is "WorldMarketOrder" or "WorldPersonalOrder") && _marketMembershipWrite)
                                {
                                    _pendingChanges.Add(new("WorldMarketMembership", id));
                                }
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
                                    action == SQLitePCL.raw.SQLITE_UPDATE
                                    && table is "WorldMarketOrder" or "WorldPersonalOrder"
                                    && column is "StationId" or "SystemId" or "RegionId" or "Bid"
                                )
                                {
                                    _marketMembershipWrite = true;
                                }
                                if (
                                    action == SQLitePCL.raw.SQLITE_UPDATE
                                    && (
                                        table == "WorldMarketOrder" && column is not ("Remaining" or "Entered" or "Issued")
                                        || table == "WorldPersonalOrder" && column is "StationId" or "SystemId" or "RegionId" or "Bid"
                                    )
                                )
                                {
                                    _marketDefinitionWrite = true;
                                }
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
                        world.SqlClosed(_changes, participants, _projectionSystem, committedSystems);
                        foreach (var pair in _committedInstitutional)
                        {
                            world.InstitutionalSupply[pair.Key] = pair.Value;
                        }
                        _committedInstitutional.Clear();
                        _pendingInstitutional.Clear();
                        _pendingChanges.Clear();
                        _changes.Clear();
                        _itemMembershipWrite = false;
                        _positionMembershipWrite = false;
                        _schemaWrite = false;
                        _marketDefinitionWrite = false;
                        _marketMembershipWrite = false;
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _simulation, null)?.Dispose();
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
