using Eden.Data.World.Market;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private const int NpcMarketRebaseRowsPerCheckpoint = 16_384;
    private const int SqliteConstraintError = 19;

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
            var projection = saved with { Rows = rows };
            try
            {
                // The owner connection observes data_version from other connections.
                // Writing here avoids making a clean generation due for another save.
                using WorldDbContext world = new(compatibility);
                using var transaction = world.Database.BeginTransaction();
                projection.Apply(world);
                transaction.Commit();
                market.Projected(world, projection);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteConstraintError)
            {
                // A stale compatibility row may still occupy a replaced assortment slot.
                // Leave this batch for the regular projection with its dependent changes.
            }
        }
    }
}
