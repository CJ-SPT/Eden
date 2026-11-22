using Eden.Data.World.Characters;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private const int NpcMarketRebaseRowsPerCheckpoint = 16_384;
    private const int HistoryRebaseRowsPerCheckpoint = 16_384;
    private const int SqliteConstraintError = 19;
    private int _skillHistoryRebaseOffset;
    private int _marketTradeRebaseOffset;

    internal void RebaseSavedNpcMarketOrders(SqliteConnection compatibility, EconomyProjection<EconomyNpcMarketOrder> saved)
    {
        var market = (EconomyTable<NpcMarketOrder, EconomyNpcMarketOrder>)_tables[typeof(NpcMarketOrder)];
        // The durable generation is already on disk. Refresh only matching rows in the
        // live SQLite compatibility image so later checkpoints copy them with the base.
        // Yield ownership between batches and leave later mutations pending.
        foreach (
            var batch in saved.Rows.Where(row => row.Value is not null).Take(NpcMarketRebaseRowsPerCheckpoint).Chunk(ProjectionBatchSize)
        )
        {
            using var owner = _owner.EnterPersistence("checkpoint market rebase");
            var rows = batch.Where(market.HasPendingProjection).ToArray();
            if (rows.Length == 0)
            {
                continue;
            }
            ApplySavedRows(compatibility, market, saved with { Rows = rows });
        }
    }

    internal void RebaseSavedHistories(SqliteConnection compatibility, EconomyCheckpoint? saved)
    {
        if (saved is null)
        {
            return;
        }
        foreach (var projection in saved.Tables)
        {
            if (projection is EconomyProjection<EconomyTrainingLog> history)
            {
                RebaseHistory<TrainingLog, EconomyTrainingLog>(compatibility, history, row => row.PilotId, ref _skillHistoryRebaseOffset);
            }
            else if (projection is EconomyProjection<EconomyMarketExecution> trades)
            {
                RebaseHistory<MarketExecution, EconomyMarketExecution>(
                    compatibility,
                    trades,
                    row => row.PilotId,
                    ref _marketTradeRebaseOffset
                );
            }
        }
    }

    private void RebaseHistory<TRow, TState>(
        SqliteConnection compatibility,
        EconomyProjection<TState> saved,
        Func<TState, long> pilotId,
        ref int offset
    )
        where TRow : class
        where TState : class
    {
        var history = (EconomyTable<TRow, TState>)_tables[typeof(TRow)];
        var pilots = (EconomyTable<Pilot, EconomyPilot>)_tables[typeof(Pilot)];
        int start = offset < saved.Count ? offset : 0;
        int count = Math.Min(saved.Count - start, HistoryRebaseRowsPerCheckpoint);
        // Unprojected pilot parents can keep old history dirty for many saves.
        // Resume the bounded scan past those rows on the next checkpoint.
        offset = start + count < saved.Count ? start + count : 0;
        foreach (var batch in saved.Rows.Skip(start).Take(count).Chunk(ProjectionBatchSize))
        {
            using var owner = _owner.EnterPersistence("checkpoint history rebase");
            var rows = batch.Where(row => history.HasPendingInsert(row) && pilots.HasSqlRow(new(pilotId(row.Value!)))).ToArray();
            if (rows.Length == 0)
            {
                continue;
            }
            // A custom trigger could skip an insert or change other compatibility rows.
            using var command = compatibility.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='trigger' AND tbl_name=$table LIMIT 1";
            command.Parameters.AddWithValue("$table", saved.Table);
            if (command.ExecuteScalar() is not null)
            {
                return;
            }
            ApplySavedRows(compatibility, history, saved with { Rows = rows });
        }
    }

    private static void ApplySavedRows(SqliteConnection compatibility, IEconomyTable table, IEconomyProjection projection)
    {
        try
        {
            // The owner connection observes data_version from other connections.
            // Writing here avoids making a clean generation due for another save.
            using WorldDbContext world = new(compatibility);
            using var transaction = world.Database.BeginTransaction();
            projection.Apply(world);
            transaction.Commit();
            table.Projected(world, projection);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteConstraintError)
        {
            // Leave this batch for the regular projection with its dependent changes.
        }
    }
}
