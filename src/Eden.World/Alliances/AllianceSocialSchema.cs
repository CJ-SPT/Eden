using Eden.Data.World.Alliances;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Alliances;

internal static class AllianceSocialSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedAllianceSocial>().ToTable("WorldAllianceSocial").HasKey(row => row.AllianceId);
        model.Entity<SavedAllianceSocial>().Property(row => row.AllianceId).ValueGeneratedNever();
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldAllianceSocial(
                AllianceId INTEGER PRIMARY KEY, Revision INTEGER NOT NULL, DataJson TEXT NOT NULL);
            """
        );
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldMail')")
                .AsEnumerable()
                .Contains("OrganizationId")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldMail ADD COLUMN OrganizationId INTEGER");
        }
    }
}
