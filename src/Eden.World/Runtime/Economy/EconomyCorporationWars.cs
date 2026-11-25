using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    internal sealed record WarState(
        long Id,
        int AttackerId,
        int DefenderId,
        long? HeadquartersId,
        long DeclaredAt,
        long StartsAt,
        long? EndingAt,
        long? EndsAt,
        long? RetractedAt,
        int? RetractedBy,
        long? BillId,
        long? BillIssuedAt,
        long? BillDueAt,
        bool Mutual,
        CorporationWarEndReason? ReasonEnded,
        long? InheritedFromWarId,
        long? InheritedAt
    );

    private sealed record WarInvitationState(int FromOwnerId, int ToOwnerId, long SentAt);

    private sealed record WarInvitationSettingsState(int OwnerId);

    internal bool MayHaveCorporationWars() =>
        ((EconomyTable<SavedCorporationWar, WarState>)_tables[typeof(SavedCorporationWar)]).RowCount > 0;

    internal static (int, int) WarPair(int first, int second) => first < second ? (first, second) : (second, first);

    private void RegisterCorporationWars()
    {
        Register(
            new EconomyTable<SavedCorporationWar, WarState>(
                CorporationWarSchema.WarTable,
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.AttackerId,
                        row.DefenderId,
                        row.HeadquartersId,
                        row.DeclaredAt,
                        row.StartsAt,
                        row.EndingAt,
                        row.EndsAt,
                        row.RetractedAt,
                        row.RetractedBy,
                        row.BillId,
                        row.BillIssuedAt,
                        row.BillDueAt,
                        row.Mutual,
                        row.ReasonEnded,
                        row.InheritedFromWarId,
                        row.InheritedAt
                    ),
                row =>
                    new()
                    {
                        Id = row.Id,
                        AttackerId = row.AttackerId,
                        DefenderId = row.DefenderId,
                        HeadquartersId = row.HeadquartersId,
                        DeclaredAt = row.DeclaredAt,
                        StartsAt = row.StartsAt,
                        EndingAt = row.EndingAt,
                        EndsAt = row.EndsAt,
                        RetractedAt = row.RetractedAt,
                        RetractedBy = row.RetractedBy,
                        BillId = row.BillId,
                        BillIssuedAt = row.BillIssuedAt,
                        BillDueAt = row.BillDueAt,
                        Mutual = row.Mutual,
                        ReasonEnded = row.ReasonEnded,
                        InheritedFromWarId = row.InheritedFromWarId,
                        InheritedAt = row.InheritedAt,
                    },
                ProjectWars,
                "Id,AttackerId,DefenderId,HeadquartersId,DeclaredAt,StartsAt,EndingAt,EndsAt,RetractedAt,RetractedBy,"
                    + "BillId,BillIssuedAt,BillDueAt,Mutual,ReasonEnded,InheritedFromWarId,InheritedAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        reader.IsDBNull(8) ? null : reader.GetInt64(8),
                        reader.IsDBNull(9) ? null : reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt32(10),
                        reader.IsDBNull(11) ? null : reader.GetInt64(11),
                        reader.IsDBNull(12) ? null : reader.GetInt64(12),
                        reader.IsDBNull(13) ? null : reader.GetInt64(13),
                        reader.GetBoolean(14),
                        reader.IsDBNull(15) ? null : (CorporationWarEndReason)reader.GetInt32(15),
                        reader.IsDBNull(16) ? null : reader.GetInt64(16),
                        reader.IsDBNull(17) ? null : reader.GetInt64(17)
                    ),
                row => new(row.Id),
                indexes:
                [
                    new("WarPair", row => WarPair(row.AttackerId, row.DefenderId)),
                    new("AttackerId", row => row.AttackerId),
                    new("DefenderId", row => row.DefenderId),
                    new("HeadquartersId", row => row.HeadquartersId),
                    new("BillId", row => row.BillId),
                    new("EndsAt", row => row.EndsAt),
                ],
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedCorporationWarInvitation, WarInvitationState>(
                CorporationWarSchema.InvitationTable,
                row => new(row.FromOwnerId, row.ToOwnerId),
                row => new(row.FromOwnerId, row.ToOwnerId, row.SentAt),
                row =>
                    new()
                    {
                        FromOwnerId = row.FromOwnerId,
                        ToOwnerId = row.ToOwnerId,
                        SentAt = row.SentAt,
                    },
                ProjectWarInvitations,
                "FromOwnerId,ToOwnerId,SentAt",
                reader => new(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3)),
                row => new(row.FromOwnerId, row.ToOwnerId),
                indexes: [new("FromOwnerId", row => row.FromOwnerId), new("ToOwnerId", row => row.ToOwnerId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedCorporationWarInvitationSettings, WarInvitationSettingsState>(
                CorporationWarSchema.SettingsTable,
                row => new(row.OwnerId),
                row => new(row.OwnerId),
                row => new() { OwnerId = row.OwnerId },
                ProjectWarInvitationSettings,
                "OwnerId",
                reader => new(reader.GetInt32(1)),
                row => new(row.OwnerId),
                primaryColumn: "OwnerId",
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectWars(WorldDbContext world, IReadOnlyList<EconomyChange<WarState>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationWar WHERE Id IN (SELECT value FROM json_each({deleted}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationWar(
                    Id,AttackerId,DefenderId,HeadquartersId,DeclaredAt,StartsAt,EndingAt,EndsAt,
                    RetractedAt,RetractedBy,BillId,BillIssuedAt,BillDueAt,Mutual,ReasonEnded,InheritedFromWarId,InheritedAt)
                SELECT value->>'$.Id',value->>'$.AttackerId',value->>'$.DefenderId',value->>'$.HeadquartersId',
                    value->>'$.DeclaredAt',value->>'$.StartsAt',value->>'$.EndingAt',value->>'$.EndsAt',
                    value->>'$.RetractedAt',value->>'$.RetractedBy',value->>'$.BillId',
                    value->>'$.BillIssuedAt',value->>'$.BillDueAt',value->>'$.Mutual',value->>'$.ReasonEnded',
                    value->>'$.InheritedFromWarId',value->>'$.InheritedAt'
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    DefenderId=excluded.DefenderId,
                    EndingAt=excluded.EndingAt,EndsAt=excluded.EndsAt,RetractedAt=excluded.RetractedAt,
                    RetractedBy=excluded.RetractedBy,BillId=excluded.BillId,BillIssuedAt=excluded.BillIssuedAt,
                    BillDueAt=excluded.BillDueAt,ReasonEnded=excluded.ReasonEnded
                """
            );
        }
    }

    private static void ProjectWarInvitations(WorldDbContext world, IReadOnlyList<EconomyChange<WarInvitationState>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldCorporationWarInvitation
                WHERE (FromOwnerId,ToOwnerId) IN (SELECT value->>'$.Id',value->>'$.Part' FROM json_each({deleted}))
                """
            );
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationWarInvitation(FromOwnerId,ToOwnerId,SentAt)
                SELECT value->>'$.FromOwnerId',value->>'$.ToOwnerId',value->>'$.SentAt' FROM json_each({rows}) WHERE 1
                ON CONFLICT(FromOwnerId,ToOwnerId) DO UPDATE SET SentAt=excluded.SentAt
                """
            );
        }
    }

    private static void ProjectWarInvitationSettings(WorldDbContext world, IReadOnlyList<EconomyChange<WarInvitationSettingsState>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql(
                $"DELETE FROM WorldCorporationWarInvitationSettings WHERE OwnerId IN (SELECT value FROM json_each({deleted}))"
            );
            string added = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Key.Id));
            world.Database.ExecuteSql(
                $"INSERT OR IGNORE INTO WorldCorporationWarInvitationSettings(OwnerId) SELECT value FROM json_each({added})"
            );
        }
    }
}

internal sealed partial class EconomyTransaction
{
    private EconomySet<SavedCorporationWar, ResidentEconomy.WarState> CorporationWarRows =>
        (EconomySet<SavedCorporationWar, ResidentEconomy.WarState>)Set<SavedCorporationWar>();

    internal bool MayHaveCorporationWars() => store.MayHaveCorporationWars() || HasPrivateRows<SavedCorporationWar>();

    internal bool ActiveCorporationWar(int first, int second, long now)
    {
        var pair = ResidentEconomy.WarPair(first, second);
        return CorporationWarRows
            .IndexedSnapshots("WarPair", pair)
            .Any(war => ResidentEconomy.WarPair(war.AttackerId, war.DefenderId) == pair && Active(war, now));
    }

    internal bool ParticipatesInCorporationWar(int owner, long now) =>
        CorporationWarRows
            .IndexedSnapshots(nameof(SavedCorporationWar.AttackerId), owner)
            .Any(war => war.AttackerId == owner && Active(war, now))
        || CorporationWarRows
            .IndexedSnapshots(nameof(SavedCorporationWar.DefenderId), owner)
            .Any(war => war.DefenderId == owner && Active(war, now));

    private static bool Active(ResidentEconomy.WarState row, long now) =>
        row.StartsAt <= now && ((row.EndsAt ?? row.BillDueAt + CorporationWarRules.Cooldown) is not long ends || now < ends);
}
