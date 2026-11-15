using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyCorporationBlueprintLock(
    long ItemId,
    int CorporationId,
    long OfficeId,
    int StationId,
    int TypeId,
    int FlagId,
    long VoteId,
    bool Permanent
);

internal sealed partial class ResidentEconomy
{
    private void RegisterCorporationBlueprintLocks() =>
        Register(
            new EconomyTable<SavedCorporationBlueprintLock, EconomyCorporationBlueprintLock>(
                "WorldCorporationBlueprintLock",
                row => new(row.ItemId),
                row => new(row.ItemId, row.CorporationId, row.OfficeId, row.StationId, row.TypeId, row.FlagId, row.VoteId, row.Permanent),
                state => new SavedCorporationBlueprintLock
                {
                    ItemId = state.ItemId,
                    CorporationId = state.CorporationId,
                    OfficeId = state.OfficeId,
                    StationId = state.StationId,
                    TypeId = state.TypeId,
                    FlagId = state.FlagId,
                    VoteId = state.VoteId,
                    Permanent = state.Permanent,
                },
                ProjectCorporationBlueprintLocks,
                "ItemId,CorporationId,OfficeId,StationId,TypeId,FlagId,VoteId,Permanent",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetBoolean(8)
                    ),
                state => new(state.ItemId),
                indexes: [new("CorporationId", row => row.CorporationId)]
            )
        );

    private static void ProjectCorporationBlueprintLocks(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationBlueprintLock>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql(
                $"DELETE FROM WorldCorporationBlueprintLock WHERE ItemId IN (SELECT value FROM json_each({removed}))"
            );
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationBlueprintLock(ItemId,CorporationId,OfficeId,StationId,TypeId,FlagId,VoteId,Permanent)
                SELECT json_extract(value,'$.ItemId'),json_extract(value,'$.CorporationId'),json_extract(value,'$.OfficeId'),
                    json_extract(value,'$.StationId'),json_extract(value,'$.TypeId'),json_extract(value,'$.FlagId'),
                    json_extract(value,'$.VoteId'),json_extract(value,'$.Permanent') FROM json_each({rows}) WHERE 1
                ON CONFLICT(ItemId) DO UPDATE SET CorporationId=excluded.CorporationId,OfficeId=excluded.OfficeId,
                    StationId=excluded.StationId,TypeId=excluded.TypeId,FlagId=excluded.FlagId,
                    VoteId=excluded.VoteId,Permanent=excluded.Permanent
                """
            );
        }
    }
}

internal sealed partial class EconomyTransaction
{
    private void ValidateCorporationBlueprintLocks()
    {
        if (!_sets.ContainsKey(typeof(OwnedItem)) && !_sets.ContainsKey(typeof(SavedCorporationBlueprintLock)))
        {
            return;
        }
        foreach (var locked in Retained<SavedCorporationBlueprintLock>())
        {
            if (Set<OwnedItem>().Find(locked.ItemId) is null || Set<SavedCorporationVote>().Find(locked.VoteId) is null)
            {
                throw new InvalidDataException("A corporation blueprint lock has no original item or vote.");
            }
        }
        foreach (var item in Set<OwnedItem>().Local.ToArray())
        {
            if (
                Set<SavedCorporationBlueprintLock>().Find(item.Id) is { } locked
                && (
                    Set<OwnedItem>().State(item) == EntityState.Deleted
                    || item.OwnerId != locked.CorporationId
                    || item.LocationId != locked.OfficeId
                    || item.TypeId != locked.TypeId
                    || item.FlagId != locked.FlagId
                    || item.Quantity != 1
                    || item.BlueprintRuns >= 0
                )
            )
            {
                throw new InvalidDataException("This original blueprint is locked by a corporation vote and cannot be moved or removed.");
            }
        }
    }
}
