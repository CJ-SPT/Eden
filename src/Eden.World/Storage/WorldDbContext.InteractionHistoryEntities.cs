using Eden.Data.World.Characters;
using Eden.World.Characters;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage;

public sealed partial class WorldDbContext
{
    private static void ConfigureInteractionHistory(ModelBuilder model)
    {
        model.Entity<SavedCombatReport>().ToTable("WorldCombatReport").HasKey(r => r.Id);
        model.Entity<SavedCombatReport>().HasOne<Pilot>().WithMany().HasForeignKey(r => r.PilotId);
        model.Entity<SavedCombatReport>().HasIndex(r => r.SourceKey).IsUnique();
        model
            .Entity<SavedCombatReport>()
            .HasIndex(r => new
            {
                r.PilotId,
                r.IsLoss,
                r.Id,
            });
        model.Entity<SavedCombatDamage>().ToTable("WorldCombatDamage").HasKey(d => new { d.VictimId, d.AttackerId });
        model.Entity<SavedNpcStanding>().ToTable("WorldNpcStanding").HasKey(s => new { s.PilotId, s.SourceId });
        model.Entity<SavedNpcStanding>().HasOne<Pilot>().WithMany().HasForeignKey(s => s.PilotId);
        model.Entity<SavedStandingTransaction>().ToTable("WorldStandingTransaction").HasKey(t => t.Id);
        model.Entity<SavedStandingTransaction>().HasOne<Pilot>().WithMany().HasForeignKey(t => t.PilotId);
        model
            .Entity<SavedStandingTransaction>()
            .HasIndex(t => new
            {
                t.PilotId,
                t.SourceId,
                t.Id,
            });
    }
}
