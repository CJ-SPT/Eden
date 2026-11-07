using Eden.Data.World.Configuration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Configuration;

internal static class WorldConfigurationSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedConfigurationRevision>().ToTable("WorldConfigurationRevision").HasKey(revision => revision.Id);
        model.Entity<SavedConfigurationRevision>().Property(revision => revision.Id).ValueGeneratedNever();
        model
            .Entity<SavedConfigurationRevision>()
            .HasOne<SavedConfigurationRevision>()
            .WithMany()
            .HasForeignKey(revision => revision.ParentId);
        model.Entity<SavedConfigurationState>().ToTable("WorldConfigurationState").HasKey(state => state.Id);
        model
            .Entity<SavedConfigurationState>()
            .HasOne<SavedConfigurationRevision>()
            .WithMany()
            .HasForeignKey(state => state.ActiveRevisionId);
        model
            .Entity<SavedConfigurationState>()
            .HasOne<SavedConfigurationRevision>()
            .WithMany()
            .HasForeignKey(state => state.PendingRevisionId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldConfigurationRevision(
                Id INTEGER PRIMARY KEY, ParentId INTEGER REFERENCES WorldConfigurationRevision(Id),
                Author TEXT NOT NULL, CreatedAt INTEGER NOT NULL, Document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldConfigurationState(
                Id INTEGER PRIMARY KEY CHECK(Id=1), Generation INTEGER NOT NULL,
                ActiveRevisionId INTEGER NOT NULL REFERENCES WorldConfigurationRevision(Id),
                PendingRevisionId INTEGER REFERENCES WorldConfigurationRevision(Id), ActivationFailure TEXT);
            CREATE TRIGGER IF NOT EXISTS ConfigurationRevisionImmutableUpdate
                BEFORE UPDATE ON WorldConfigurationRevision BEGIN SELECT RAISE(ABORT,'Configuration revisions are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS ConfigurationRevisionImmutableDelete
                BEFORE DELETE ON WorldConfigurationRevision BEGIN SELECT RAISE(ABORT,'Configuration revisions are immutable'); END;
            """
        );
}
