using Eden.Data.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

internal static class CorporationWarSchema
{
    internal const int Version = 227;
    internal const string WarTable = "WorldCorporationWar";
    internal const string InvitationTable = "WorldCorporationWarInvitation";
    internal const string SettingsTable = "WorldCorporationWarInvitationSettings";
    internal const string NegotiationTable = "WorldCorporationWarNegotiation";
    internal const string AllyTable = "WorldCorporationWarAlly";

    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedCorporationWar>().ToTable(WarTable).HasKey(row => row.Id);
        model.Entity<SavedCorporationWar>().HasIndex(row => row.AttackerId);
        model.Entity<SavedCorporationWar>().HasIndex(row => row.DefenderId);
        model.Entity<SavedCorporationWar>().HasIndex(row => row.HeadquartersId);
        model.Entity<SavedCorporationWar>().HasIndex(row => row.BillId);
        model.Entity<SavedCorporationWarInvitation>().ToTable(InvitationTable).HasKey(row => new { row.FromOwnerId, row.ToOwnerId });
        model.Entity<SavedCorporationWarInvitation>().HasIndex(row => row.ToOwnerId);
        model.Entity<SavedCorporationWarInvitationSettings>().ToTable(SettingsTable).HasKey(row => row.OwnerId);
        model.Entity<SavedCorporationWarNegotiation>().ToTable(NegotiationTable).HasKey(row => row.Id);
        model.Entity<SavedCorporationWarNegotiation>().HasIndex(row => row.WarId);
        model.Entity<SavedCorporationWarNegotiation>().HasIndex(row => row.FromOwnerId);
        model.Entity<SavedCorporationWarNegotiation>().HasIndex(row => row.ToOwnerId);
        model.Entity<SavedCorporationWarAlly>().ToTable(AllyTable).HasKey(row => new { row.WarId, row.OwnerId });
        model.Entity<SavedCorporationWarAlly>().HasIndex(row => row.OwnerId);
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporationWar(
                Id INTEGER PRIMARY KEY, AttackerId INTEGER NOT NULL, DefenderId INTEGER NOT NULL,
                HeadquartersId INTEGER, DeclaredAt INTEGER NOT NULL, StartsAt INTEGER NOT NULL,
                EndingAt INTEGER, EndsAt INTEGER, RetractedAt INTEGER, RetractedBy INTEGER,
                BillId INTEGER, BillIssuedAt INTEGER, BillDueAt INTEGER, Mutual INTEGER NOT NULL,
                ReasonEnded INTEGER, InheritedFromWarId INTEGER, InheritedAt INTEGER, OpenForAllies INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationWar_AttackerId ON WorldCorporationWar(AttackerId);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationWar_DefenderId ON WorldCorporationWar(DefenderId);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationWar_HeadquartersId ON WorldCorporationWar(HeadquartersId);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationWar_BillId ON WorldCorporationWar(BillId);
            CREATE TABLE IF NOT EXISTS WorldCorporationWarInvitation(
                FromOwnerId INTEGER NOT NULL, ToOwnerId INTEGER NOT NULL, SentAt INTEGER NOT NULL,
                PRIMARY KEY(FromOwnerId,ToOwnerId));
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationWarInvitation_ToOwnerId ON WorldCorporationWarInvitation(ToOwnerId);
            CREATE TABLE IF NOT EXISTS WorldCorporationWarInvitationSettings(OwnerId INTEGER PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS WorldCorporationWarNegotiation(
                Id INTEGER PRIMARY KEY, WarId INTEGER NOT NULL, Kind INTEGER NOT NULL, FromOwnerId INTEGER NOT NULL,
                ToOwnerId INTEGER NOT NULL, AttackerId INTEGER NOT NULL, DefenderId INTEGER NOT NULL,
                AmountCents INTEGER NOT NULL, Description TEXT NOT NULL, PayerCorporationId INTEGER, PayerDivisionKey INTEGER,
                State INTEGER NOT NULL, CreatedAt INTEGER NOT NULL, AcceptedAt INTEGER, DeclinedAt INTEGER, RetractedAt INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationWarNegotiation_WarId ON WorldCorporationWarNegotiation(WarId);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationWarNegotiation_FromOwnerId ON WorldCorporationWarNegotiation(FromOwnerId);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationWarNegotiation_ToOwnerId ON WorldCorporationWarNegotiation(ToOwnerId);
            CREATE TABLE IF NOT EXISTS WorldCorporationWarAlly(
                WarId INTEGER NOT NULL, OwnerId INTEGER NOT NULL, AttackerId INTEGER NOT NULL,
                StartsAt INTEGER NOT NULL, EndsAt INTEGER NOT NULL, PRIMARY KEY(WarId,OwnerId));
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationWarAlly_OwnerId ON WorldCorporationWarAlly(OwnerId);
            """
        );
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldCorporationWar')")
                .ToArray()
                .Contains("OpenForAllies")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCorporationWar ADD COLUMN OpenForAllies INTEGER NOT NULL DEFAULT 0");
        }
    }
}
