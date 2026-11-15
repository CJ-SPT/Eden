using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

internal static class AbyssalSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedAbyssalRun>().ToTable("WorldAbyssalRun").HasKey(row => row.Id);
        model.Entity<SavedAbyssalParticipant>().ToTable("WorldAbyssalParticipant").HasKey(row => row.PilotId);
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldAbyssalRun (
                Id INTEGER NOT NULL PRIMARY KEY,
                OwnerPilotId INTEGER NOT NULL,
                SystemId INTEGER NOT NULL,
                OriginSystemId INTEGER NOT NULL,
                OriginX REAL NOT NULL,
                OriginY REAL NOT NULL,
                OriginZ REAL NOT NULL,
                FilamentTypeId INTEGER NOT NULL,
                Mode INTEGER NOT NULL,
                Tier INTEGER NOT NULL,
                Weather INTEGER NOT NULL,
                Room INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL,
                ExpiresAt INTEGER NOT NULL,
                Status INTEGER NOT NULL,
                Seed INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS WorldAbyssalParticipant (
                PilotId INTEGER NOT NULL PRIMARY KEY,
                RunId INTEGER NOT NULL,
                ShipId INTEGER NOT NULL,
                Finished INTEGER NOT NULL
            );
            """
        );
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldAbyssalRun')").Contains("EnvironmentAt"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldAbyssalRun ADD COLUMN EnvironmentAt INTEGER NOT NULL DEFAULT 0");
        }
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldAbyssalParticipant')")
                .Contains("OutsideSince")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldAbyssalParticipant ADD COLUMN OutsideSince INTEGER NOT NULL DEFAULT 0");
        }
        if (
            !world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldAbyssalRun')").Contains("DisabledFields")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldAbyssalRun ADD COLUMN DisabledFields INTEGER NOT NULL DEFAULT 0");
        }
    }
}
