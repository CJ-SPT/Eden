using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

internal static class PersonalSkillPlanSchema
{
    internal static void Configure(ModelBuilder model)
    {
        var row = model.Entity<SavedPersonalSkillPlans>();
        row.ToTable("WorldPersonalSkillPlans").HasKey(plans => plans.PilotId);
        row.HasOne<Pilot>().WithOne().HasForeignKey<SavedPersonalSkillPlans>(plans => plans.PilotId);
        row.Property(plans => plans.Book).HasColumnName("Payload").HasConversion(book => WriteBook(book), payload => ReadBook(payload));
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldPersonalSkillPlans(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                Payload TEXT NOT NULL);
            """
        );

    internal static SavedSkillPlanBook ReadBook(string payload) =>
        JsonSerializer.Deserialize<SavedSkillPlanBook>(payload) ?? throw new InvalidDataException("Invalid saved skill plan book.");

    private static string WriteBook(SavedSkillPlanBook book) => JsonSerializer.Serialize(book);
}
