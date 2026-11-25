using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    internal sealed record WarAllyState(long WarId, int OwnerId, int AttackerId, long StartsAt, long EndsAt);

    private sealed record NegotiationState(
        long Id,
        long WarId,
        CorporationWarNegotiationKind Kind,
        int FromOwnerId,
        int ToOwnerId,
        int AttackerId,
        int DefenderId,
        long AmountCents,
        string Description,
        int? PayerCorporationId,
        int? PayerDivisionKey,
        CorporationWarNegotiationState State,
        long CreatedAt,
        long? AcceptedAt,
        long? DeclinedAt,
        long? RetractedAt
    );

    private void RegisterCorporationWarNegotiations()
    {
        Register(
            new EconomyTable<SavedCorporationWarNegotiation, NegotiationState>(
                CorporationWarSchema.NegotiationTable,
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.WarId,
                        row.Kind,
                        row.FromOwnerId,
                        row.ToOwnerId,
                        row.AttackerId,
                        row.DefenderId,
                        row.AmountCents,
                        row.Description,
                        row.PayerCorporationId,
                        row.PayerDivisionKey,
                        row.State,
                        row.CreatedAt,
                        row.AcceptedAt,
                        row.DeclinedAt,
                        row.RetractedAt
                    ),
                row =>
                    new()
                    {
                        Id = row.Id,
                        WarId = row.WarId,
                        Kind = row.Kind,
                        FromOwnerId = row.FromOwnerId,
                        ToOwnerId = row.ToOwnerId,
                        AttackerId = row.AttackerId,
                        DefenderId = row.DefenderId,
                        AmountCents = row.AmountCents,
                        Description = row.Description,
                        PayerCorporationId = row.PayerCorporationId,
                        PayerDivisionKey = row.PayerDivisionKey,
                        State = row.State,
                        CreatedAt = row.CreatedAt,
                        AcceptedAt = row.AcceptedAt,
                        DeclinedAt = row.DeclinedAt,
                        RetractedAt = row.RetractedAt,
                    },
                ProjectWarNegotiations,
                "Id,WarId,Kind,FromOwnerId,ToOwnerId,AttackerId,DefenderId,AmountCents,Description,PayerCorporationId,PayerDivisionKey,"
                    + "State,CreatedAt,AcceptedAt,DeclinedAt,RetractedAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        (CorporationWarNegotiationKind)reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt64(8),
                        reader.GetString(9),
                        reader.IsDBNull(10) ? null : reader.GetInt32(10),
                        reader.IsDBNull(11) ? null : reader.GetInt32(11),
                        (CorporationWarNegotiationState)reader.GetInt32(12),
                        reader.GetInt64(13),
                        reader.IsDBNull(14) ? null : reader.GetInt64(14),
                        reader.IsDBNull(15) ? null : reader.GetInt64(15),
                        reader.IsDBNull(16) ? null : reader.GetInt64(16)
                    ),
                row => new(row.Id),
                indexes:
                [
                    new("WarId", row => row.WarId),
                    new("FromOwnerId", row => row.FromOwnerId),
                    new("ToOwnerId", row => row.ToOwnerId),
                ],
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedCorporationWarAlly, WarAllyState>(
                CorporationWarSchema.AllyTable,
                row => new(row.WarId, row.OwnerId),
                row => new(row.WarId, row.OwnerId, row.AttackerId, row.StartsAt, row.EndsAt),
                row =>
                    new()
                    {
                        WarId = row.WarId,
                        OwnerId = row.OwnerId,
                        AttackerId = row.AttackerId,
                        StartsAt = row.StartsAt,
                        EndsAt = row.EndsAt,
                    },
                ProjectWarAllies,
                "WarId,OwnerId,AttackerId,StartsAt,EndsAt",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4), reader.GetInt64(5)),
                row => new(row.WarId, row.OwnerId),
                indexes:
                [
                    new("WarId", row => row.WarId),
                    new("OwnerId", row => row.OwnerId),
                    new("AllyPair", row => WarPair(row.OwnerId, row.AttackerId)),
                ],
                primaryColumn: null
            )
        );
    }

    private static void ProjectWarNegotiations(WorldDbContext world, IReadOnlyList<EconomyChange<NegotiationState>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationWarNegotiation WHERE Id IN (SELECT value FROM json_each({deleted}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationWarNegotiation(
                    Id,WarId,Kind,FromOwnerId,ToOwnerId,AttackerId,DefenderId,AmountCents,Description,PayerCorporationId,
                    PayerDivisionKey,State,CreatedAt,AcceptedAt,DeclinedAt,RetractedAt)
                SELECT value->>'$.Id',value->>'$.WarId',value->>'$.Kind',value->>'$.FromOwnerId',value->>'$.ToOwnerId',
                    value->>'$.AttackerId',value->>'$.DefenderId',value->>'$.AmountCents',value->>'$.Description',
                    value->>'$.PayerCorporationId',value->>'$.PayerDivisionKey',value->>'$.State',value->>'$.CreatedAt',
                    value->>'$.AcceptedAt',value->>'$.DeclinedAt',value->>'$.RetractedAt'
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET State=excluded.State,AcceptedAt=excluded.AcceptedAt,DeclinedAt=excluded.DeclinedAt,
                    RetractedAt=excluded.RetractedAt
                """
            );
        }
    }

    private static void ProjectWarAllies(WorldDbContext world, IReadOnlyList<EconomyChange<WarAllyState>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldCorporationWarAlly
                WHERE (WarId,OwnerId) IN (SELECT value->>'$.Id',value->>'$.Part' FROM json_each({deleted}))
                """
            );
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationWarAlly(WarId,OwnerId,AttackerId,StartsAt,EndsAt)
                SELECT value->>'$.WarId',value->>'$.OwnerId',value->>'$.AttackerId',value->>'$.StartsAt',value->>'$.EndsAt'
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(WarId,OwnerId) DO UPDATE SET StartsAt=excluded.StartsAt,EndsAt=excluded.EndsAt
                """
            );
        }
    }
}

internal sealed partial class EconomyTransaction
{
    private EconomySet<SavedCorporationWarAlly, ResidentEconomy.WarAllyState> WarAllyRows =>
        (EconomySet<SavedCorporationWarAlly, ResidentEconomy.WarAllyState>)Set<SavedCorporationWarAlly>();

    internal bool ActiveWarAgainstAlly(int first, int second, long now)
    {
        var pair = ResidentEconomy.WarPair(first, second);
        return WarAllyRows
            .IndexedSnapshots("AllyPair", pair)
            .Any(ally => ResidentEconomy.WarPair(ally.OwnerId, ally.AttackerId) == pair && ActiveAlly(ally, now));
    }

    internal bool ParticipatesAsWarAlly(int owner, long now) =>
        WarAllyRows
            .IndexedSnapshots(nameof(SavedCorporationWarAlly.OwnerId), owner)
            .Any(ally => ally.OwnerId == owner && ActiveAlly(ally, now));

    private bool ActiveAlly(ResidentEconomy.WarAllyState ally, long now) =>
        ally.StartsAt <= now && now < ally.EndsAt && CorporationWarRows.ReadSnapshot(new(ally.WarId)) is { } war && Active(war, now);
}
