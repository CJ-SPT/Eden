using System.Data.Common;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Eden.World.Market;

internal sealed record InstitutionalPreparation(int StationId, long Catalog, int Stock);

// Preparation belongs to the exact EF transaction, including read-only transactions
// for which SQLite need not invoke its native write-commit hook.
internal sealed class MarketPreparationTransactions(WorldDbContext world) : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        WorldConnections.CompleteInstitutional(world, eventData.TransactionId, committed: true);

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        WorldConnections.CompleteInstitutional(world, eventData.TransactionId, committed: false);
}
