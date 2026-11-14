using System.Text.Json;
using Eden.Data.World.Market;
using Eden.Data.World.Redemption;
using Eden.Gameplay.Redemption;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedRedeemToken(
    long Id,
    long AccountId,
    string Reference,
    int TypeId,
    int Quantity,
    string Label,
    string Description,
    int? StationId,
    long CreatedAt,
    long? ExpiresAt,
    RedeemTokenState State,
    long? FinishedAt,
    long? PilotId,
    long? ItemId,
    long? ReversedAt
);

internal sealed record EconomySavedRedeemedCurrency(
    long TokenId,
    long PilotId,
    RedeemCurrencyKind Kind,
    long Amount,
    long Before,
    long After,
    long Time
);

internal sealed partial class ResidentEconomy
{
    private void RegisterRedemption()
    {
        Register(
            new EconomyTable<SavedRedeemToken, EconomySavedRedeemToken>(
                "WorldRedeemToken",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.AccountId,
                        row.Reference,
                        row.TypeId,
                        row.Quantity,
                        row.Label,
                        row.Description,
                        row.StationId,
                        row.CreatedAt,
                        row.ExpiresAt,
                        row.State,
                        row.FinishedAt,
                        row.PilotId,
                        row.ItemId,
                        row.ReversedAt
                    ),
                state => new SavedRedeemToken
                {
                    Id = state.Id,
                    AccountId = state.AccountId,
                    Reference = state.Reference,
                    TypeId = state.TypeId,
                    Quantity = state.Quantity,
                    Label = state.Label,
                    Description = state.Description,
                    StationId = state.StationId,
                    CreatedAt = state.CreatedAt,
                    ExpiresAt = state.ExpiresAt,
                    State = state.State,
                    FinishedAt = state.FinishedAt,
                    PilotId = state.PilotId,
                    ItemId = state.ItemId,
                    ReversedAt = state.ReversedAt,
                },
                ProjectSavedRedeemToken,
                "Id,AccountId,Reference,TypeId,Quantity,Label,Description,StationId,CreatedAt,"
                    + "ExpiresAt,State,FinishedAt,PilotId,ItemId,ReversedAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetString(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        reader.IsDBNull(8) ? null : reader.GetInt32(8),
                        reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        (RedeemTokenState)reader.GetInt32(11),
                        reader.IsDBNull(12) ? null : reader.GetInt64(12),
                        reader.IsDBNull(13) ? null : reader.GetInt64(13),
                        reader.IsDBNull(14) ? null : reader.GetInt64(14),
                        reader.IsDBNull(15) ? null : reader.GetInt64(15)
                    ),
                state => new(state.Id),
                indexes: [new("AccountId", row => row.AccountId), new("ItemId", row => row.ItemId)],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedRedeemedCurrency, EconomySavedRedeemedCurrency>(
                "WorldRedeemedCurrency",
                row => new(row.TokenId),
                row => new(row.TokenId, row.PilotId, row.Kind, row.Amount, row.Before, row.After, row.Time),
                state => new SavedRedeemedCurrency
                {
                    TokenId = state.TokenId,
                    PilotId = state.PilotId,
                    Kind = state.Kind,
                    Amount = state.Amount,
                    Before = state.Before,
                    After = state.After,
                    Time = state.Time,
                },
                ProjectSavedRedeemedCurrency,
                "TokenId,PilotId,Kind,Amount,Before,After,Time",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        (RedeemCurrencyKind)reader.GetInt32(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7)
                    ),
                state => new(state.TokenId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "TokenId",
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectSavedRedeemToken(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedRedeemToken>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldRedeemToken WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldRedeemToken(
                    Id, AccountId, Reference, TypeId, Quantity, Label, Description, StationId, CreatedAt, ExpiresAt, State,
                    FinishedAt, PilotId, ItemId, ReversedAt)
                SELECT
                    json_extract(value,'$.Id'), json_extract(value,'$.AccountId'), json_extract(value,'$.Reference'),
                    json_extract(value,'$.TypeId'), json_extract(value,'$.Quantity'), json_extract(value,'$.Label'),
                    json_extract(value,'$.Description'), json_extract(value,'$.StationId'), json_extract(value,'$.CreatedAt'),
                    json_extract(value,'$.ExpiresAt'), json_extract(value,'$.State'), json_extract(value,'$.FinishedAt'),
                    json_extract(value,'$.PilotId'), json_extract(value,'$.ItemId'), json_extract(value,'$.ReversedAt')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    AccountId=excluded.AccountId, Reference=excluded.Reference, TypeId=excluded.TypeId,
                    Quantity=excluded.Quantity, Label=excluded.Label, Description=excluded.Description,
                    StationId=excluded.StationId, CreatedAt=excluded.CreatedAt, ExpiresAt=excluded.ExpiresAt,
                    State=excluded.State, FinishedAt=excluded.FinishedAt, PilotId=excluded.PilotId, ItemId=excluded.ItemId,
                    ReversedAt=excluded.ReversedAt
                """
            );
        }
    }

    private static void ProjectSavedRedeemedCurrency(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedRedeemedCurrency>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldRedeemedCurrency WHERE TokenId IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldRedeemedCurrency(
                    TokenId, PilotId, Kind, Amount, Before, After, Time)
                SELECT
                    json_extract(value,'$.TokenId'), json_extract(value,'$.PilotId'), json_extract(value,'$.Kind'),
                    json_extract(value,'$.Amount'), json_extract(value,'$.Before'), json_extract(value,'$.After'),
                    json_extract(value,'$.Time')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(TokenId) DO UPDATE SET
                    PilotId=excluded.PilotId, Kind=excluded.Kind, Amount=excluded.Amount, Before=excluded.Before,
                    After=excluded.After, Time=excluded.Time
                """
            );
        }
    }
}
