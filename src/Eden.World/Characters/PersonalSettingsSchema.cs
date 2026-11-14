using Eden.Data.World.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

internal static class PersonalSettingsSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedNotepadEntry>().ToTable("WorldNotepadEntry").HasKey(row => row.Id);
        model.Entity<SavedNotepadEntry>().HasOne<Pilot>().WithMany().HasForeignKey(row => row.PilotId);
        model.Entity<SavedNotepadEntry>().HasIndex(row => row.PilotId);
        model.Entity<SavedSharedSetting>().ToTable("WorldSharedSetting").HasKey(row => row.Id);
        model.Entity<SavedSharedSetting>().HasIndex(row => new { row.Kind, row.Hash });
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldNotepadEntry(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                Label TEXT NOT NULL, Text TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldNotepadEntry_PilotId ON WorldNotepadEntry(PilotId);
            CREATE TABLE IF NOT EXISTS WorldSharedSetting(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, Kind INTEGER NOT NULL, Hash TEXT NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldSharedSetting_Kind_Hash ON WorldSharedSetting(Kind,Hash);
            """
        );
}
