using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed partial class WorldDbContext
{
    private static void ConfigureCorporations(ModelBuilder model)
    {
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
            CREATE TABLE IF NOT EXISTS WorldCorporationEmployment(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                CorporationId INTEGER NOT NULL, StartedAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationEmployment_PilotId_Id ON WorldCorporationEmployment(PilotId,Id);
            CREATE TABLE IF NOT EXISTS WorldCorporationPayment(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                CorporationId INTEGER NOT NULL, Time INTEGER NOT NULL, BalanceCents INTEGER NOT NULL);
            """
        );
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
