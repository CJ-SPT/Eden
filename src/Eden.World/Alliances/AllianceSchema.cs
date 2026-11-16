using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Alliances;

internal static class AllianceSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedAlliance>().ToTable("WorldAlliance").HasKey(row => row.Id);
        model.Entity<SavedAlliance>().Property(row => row.Id).ValueGeneratedNever();
        model.Entity<SavedAlliance>().HasIndex(row => row.Name).IsUnique();
        model.Entity<SavedAlliance>().HasIndex(row => row.Ticker).IsUnique();
        model.Entity<SavedAllianceMember>().ToTable("WorldAllianceMember").HasKey(row => row.CorporationId);
        model.Entity<SavedAllianceMember>().Property(row => row.CorporationId).ValueGeneratedNever();
        model.Entity<SavedAllianceMember>().HasIndex(row => row.AllianceId);
        model.Entity<SavedAllianceMember>().HasOne<SavedAlliance>().WithMany().HasForeignKey(row => row.AllianceId);
        model.Entity<SavedAllianceMember>().HasOne<SavedCorporation>().WithMany().HasForeignKey(row => row.CorporationId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldAlliance(
                Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Ticker TEXT NOT NULL,
                Description TEXT NOT NULL, Url TEXT NOT NULL, CreatorId INTEGER NOT NULL,
                CreatorCorporationId INTEGER NOT NULL, ExecutorCorporationId INTEGER,
                CreatedAt INTEGER NOT NULL, ClosedAt INTEGER);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldAlliance_Name ON WorldAlliance(Name);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldAlliance_Ticker ON WorldAlliance(Ticker);
            CREATE TABLE IF NOT EXISTS WorldAllianceMember(
                CorporationId INTEGER PRIMARY KEY REFERENCES WorldCorporation(Id),
                AllianceId INTEGER NOT NULL REFERENCES WorldAlliance(Id), JoinedAt INTEGER NOT NULL, ChosenExecutorId INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldAllianceMember_AllianceId ON WorldAllianceMember(AllianceId);
            """
        );
}
