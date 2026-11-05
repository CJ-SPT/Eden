using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed partial class WorldDbContext
{
    private static void ConfigureCorporations(ModelBuilder model)
    {
        model.Entity<SavedCorporationApplication>().ToTable("WorldCorporationApplication").HasKey(a => a.Id);
        model.Entity<SavedCorporationMemberAccess>().ToTable("WorldCorporationMemberAccess").HasKey(m => m.PilotId);
        model.Entity<SavedCorporationMemberAccess>().HasOne<Pilot>().WithMany().HasForeignKey(m => m.PilotId);
        model.Entity<SavedCorporationTitle>().ToTable("WorldCorporationTitle").HasKey(t => new { t.CorporationId, t.TitleId });
        model.Entity<SavedCorporationTitle>().HasOne<SavedCorporation>().WithMany().HasForeignKey(t => t.CorporationId);
        model.Entity<SavedCorporationApplication>().HasOne<Pilot>().WithMany().HasForeignKey(a => a.PilotId);
        model.Entity<SavedCorporationApplication>().HasOne<SavedCorporation>().WithMany().HasForeignKey(a => a.CorporationId);
        model.Entity<SavedCorporationApplication>().HasIndex(a => new { a.PilotId, a.CorporationId });
        model.Entity<SavedCorporationChange>().ToTable("WorldCorporationChange").HasKey(c => c.Id);
        model.Entity<SavedCorporationChange>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.PilotId);
        model.Entity<SavedCorporationChange>().HasIndex(c => new { c.PilotId, c.Id });
        model.Entity<SavedCorporation>().ToTable("WorldCorporation").HasKey(c => c.Id);
        model.Entity<SavedCorporation>().Property(c => c.Id).ValueGeneratedNever();
        model.Entity<SavedCorporation>().Property(c => c.Name).UseCollation("NOCASE");
        model.Entity<SavedCorporation>().Property(c => c.Ticker).UseCollation("NOCASE");
        model.Entity<SavedCorporation>().HasIndex(c => c.Name).IsUnique();
        model.Entity<SavedCorporation>().HasIndex(c => c.Ticker).IsUnique();
        model.Entity<SavedCorporation>().HasIndex(c => c.CeoId).IsUnique();
        model.Entity<SavedCorporation>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.CeoId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<SavedCorporation>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.CreatorId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<SavedCorporationEmployment>().ToTable("WorldCorporationEmployment").HasKey(e => e.Id);
        model.Entity<SavedCorporationEmployment>().HasIndex(e => new { e.PilotId, e.Id });
        model.Entity<SavedCorporationEmployment>().HasOne<Pilot>().WithMany().HasForeignKey(e => e.PilotId);
        model.Entity<SavedCorporationPayment>().ToTable("WorldCorporationPayment").HasKey(p => p.Id);
        model.Entity<SavedCorporationPayment>().HasOne<Pilot>().WithMany().HasForeignKey(p => p.PilotId);
    }
}

internal static class CorporationSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporation(
                Id INTEGER PRIMARY KEY, Name TEXT NOT NULL COLLATE NOCASE, Ticker TEXT NOT NULL COLLATE NOCASE,
                DetailsJson TEXT NOT NULL, CeoId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE RESTRICT,
                CreatorId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE RESTRICT,
                StationId INTEGER NOT NULL, SystemId INTEGER NOT NULL, CreatedAt INTEGER NOT NULL,
                MemberLimit INTEGER NOT NULL, BalanceCents INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCorporation_Name ON WorldCorporation(Name);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCorporation_Ticker ON WorldCorporation(Ticker);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCorporation_CeoId ON WorldCorporation(CeoId);
            CREATE TABLE IF NOT EXISTS WorldCorporationApplication(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                Text TEXT NOT NULL, Status INTEGER NOT NULL, AppliedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL,
                Response TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationApplication_PilotId_CorporationId
                ON WorldCorporationApplication(PilotId,CorporationId);
            CREATE TABLE IF NOT EXISTS WorldCorporationChange(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ApplicationJson TEXT, MembershipJson TEXT, AdministrationJson TEXT, RoleJson TEXT, TitleJson TEXT);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationChange_PilotId_Id ON WorldCorporationChange(PilotId,Id);
            CREATE TABLE IF NOT EXISTS WorldCorporationEmployment(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                CorporationId INTEGER NOT NULL, StartedAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationEmployment_PilotId_Id ON WorldCorporationEmployment(PilotId,Id);
            CREATE TABLE IF NOT EXISTS WorldCorporationPayment(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                CorporationId INTEGER NOT NULL, Time INTEGER NOT NULL, BalanceCents INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldCorporationMemberAccess(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                CorporationId INTEGER NOT NULL, DataJson TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldCorporationTitle(
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                TitleId INTEGER NOT NULL, DataJson TEXT NOT NULL, PRIMARY KEY(CorporationId,TitleId));
            """
        );
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldCorporationChange')")
                .Contains("AdministrationJson")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCorporationChange ADD COLUMN AdministrationJson TEXT");
        }
        foreach (string column in new[] { "RoleJson", "TitleJson" })
        {
            if (
                !world
                    .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldCorporationChange')")
                    .Contains(column)
            )
            {
                world.Database.ExecuteSqlRaw(
                    column == "RoleJson"
                        ? "ALTER TABLE WorldCorporationChange ADD COLUMN RoleJson TEXT"
                        : "ALTER TABLE WorldCorporationChange ADD COLUMN TitleJson TEXT"
                );
            }
        }
        if (
            world
                .Set<SavedCorporation>()
                .Any(c => !world.Pilots.Any(p => p.Id == c.CeoId && p.CorporationId == c.Id) || c.MemberLimit < 1 || c.BalanceCents < 0)
        )
        {
            throw new InvalidDataException("Saved player corporation has an invalid CEO, capacity or wallet.");
        }
    }
}
