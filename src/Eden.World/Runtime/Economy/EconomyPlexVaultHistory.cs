using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record VaultHistoryRow(long AccountId, PlexVaultEntry Entry);

    private EconomyTable<SavedPlexVaultEntry, VaultHistoryRow> VaultHistoryTable =>
        (EconomyTable<SavedPlexVaultEntry, VaultHistoryRow>)_tables[typeof(SavedPlexVaultEntry)];

    internal PlexVaultEntry[] VaultHistory(long accountId) =>
        Inspect(_ =>
            VaultHistoryTable
                .Indexed(nameof(SavedPlexVaultEntry.AccountId), accountId)
                .OrderByDescending(key => key.Id)
                .Take(PlexVaultRules.HistoryLimit)
                .Select(key => VaultHistoryTable.Snapshot(key)!.Entry)
                .ToArray()
        );

    internal PlexVaultEntry? VaultHistoryEntry(long accountId, long id) =>
        Inspect(_ => VaultHistoryTable.Snapshot(new(id)) is { } row && row.AccountId == accountId ? row.Entry : null);

    private void RegisterPlexVaultHistory() =>
        Register(
            new EconomyTable<SavedPlexVaultEntry, VaultHistoryRow>(
                PlexVaultSchema.HistoryTable,
                row => new(row.Id),
                row => new(row.AccountId, new(row.Id, row.PilotId, row.Kind, row.Amount, row.Balance, row.Time, row.CounterpartyPilotId)),
                row => new SavedPlexVaultEntry
                {
                    Id = row.Entry.Id,
                    AccountId = row.AccountId,
                    PilotId = row.Entry.PilotId,
                    Kind = row.Entry.Kind,
                    Amount = row.Entry.Amount,
                    Balance = row.Entry.Balance,
                    Time = row.Entry.Time,
                    CounterpartyPilotId = row.Entry.CounterpartyPilotId,
                },
                ProjectPlexVaultEntries,
                "AccountId,Id,PilotId,Kind,Amount,Balance,Time,CounterpartyPilotId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        new(
                            reader.GetInt64(2),
                            reader.GetInt64(3),
                            (PlexVaultTransferKind)reader.GetInt32(4),
                            reader.GetInt64(5),
                            reader.GetInt64(6),
                            reader.GetInt64(7),
                            reader.IsDBNull(8) ? null : reader.GetInt64(8)
                        )
                    ),
                row => new(row.Entry.Id),
                indexes: [new(nameof(SavedPlexVaultEntry.AccountId), row => row.AccountId, member: EconomyIndexMember.RowId)],
                rowIdIsKey: true
            )
        );

    private static void ProjectPlexVaultEntries(WorldDbContext world, IReadOnlyList<EconomyChange<VaultHistoryRow>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(change => change.Value is null).Select(change => change.Key.Id).ToArray();
            if (removed.Length != 0)
            {
                world.Database.ExecuteSql(
                    $"DELETE FROM WorldPlexVaultHistory WHERE Id IN (SELECT value FROM json_each({JsonSerializer.Serialize(removed)}))"
                );
            }
            var rows = batch.Select(change => change.Value).OfType<VaultHistoryRow>().ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldPlexVaultHistory(Id,AccountId,PilotId,Kind,Amount,Balance,Time,CounterpartyPilotId)
                    SELECT value->>'$.Entry.Id',value->>'$.AccountId',value->>'$.Entry.PilotId',value->>'$.Entry.Kind',
                        value->>'$.Entry.Amount',value->>'$.Entry.Balance',value->>'$.Entry.Time',
                        value->>'$.Entry.CounterpartyPilotId' FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET AccountId=excluded.AccountId,PilotId=excluded.PilotId,Kind=excluded.Kind,
                        Amount=excluded.Amount,Balance=excluded.Balance,Time=excluded.Time,
                        CounterpartyPilotId=excluded.CounterpartyPilotId
                    """
                );
            }
        }
    }
}
