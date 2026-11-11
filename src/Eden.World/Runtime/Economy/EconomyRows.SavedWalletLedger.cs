using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedWalletLedger(
    long Id,
    long AmountCents,
    long BalanceCents,
    long CounterpartyId,
    Eden.Gameplay.Market.WalletEntryType EntryType,
    int Key,
    long OwnerId,
    string Reason,
    long ReferenceId,
    long Time
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedWalletLedger(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedWalletLedger>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldWalletLedger
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldWalletLedger.Id=json_extract(value,'$.Id')
                    )
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldWalletLedger(
                        Id,AmountCents,BalanceCents,CounterpartyId,EntryType,Key,OwnerId,Reason,ReferenceId,Time
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.AmountCents'),json_extract(value,'$.BalanceCents'),
                        json_extract(value,'$.CounterpartyId'),json_extract(value,'$.EntryType'),
                        json_extract(value,'$.Key'),json_extract(value,'$.OwnerId'),json_extract(value,'$.Reason'),
                        json_extract(value,'$.ReferenceId'),json_extract(value,'$.Time')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        AmountCents=excluded.AmountCents,BalanceCents=excluded.BalanceCents,
                        CounterpartyId=excluded.CounterpartyId,EntryType=excluded.EntryType,Key=excluded.Key,
                        OwnerId=excluded.OwnerId,Reason=excluded.Reason,ReferenceId=excluded.ReferenceId,Time=excluded.Time
                    """
                );
            }
        }
    }
}
