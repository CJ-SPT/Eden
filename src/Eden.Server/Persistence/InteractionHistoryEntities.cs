using System.Text.Json;
using Eden.Gameplay.Characters;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class SavedCombatReport
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public string SourceKey { get; set; } = "";
    public long Time { get; set; }
    public bool IsLoss { get; set; }
    public string Data { get; set; } = "";

    public ShipCombatReport Snapshot() =>
        (JsonSerializer.Deserialize<ShipCombatReport>(Data) ?? throw new InvalidDataException("Invalid saved combat report.")) with
        {
            Id = Id,
        };
}

public sealed class SavedCombatDamage
{
    public long VictimId { get; set; }
    public long AttackerId { get; set; }
    public string PartyJson { get; set; } = "";
    public double Damage { get; set; }
}

public sealed class SavedNpcStanding
{
    public long PilotId { get; set; }
    public long SourceId { get; set; }
    public double Value { get; set; }
}

public sealed class SavedStandingTransaction
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long SourceId { get; set; }
    public long Time { get; set; }
    public StandingEvent Event { get; set; }
    public double Before { get; set; }
    public double After { get; set; }
    public int VictimCorporationId { get; set; }
    public int SystemId { get; set; }
    public int VictimTypeId { get; set; }

    public StandingTransaction Snapshot() =>
        new(Id, PilotId, SourceId, Time, Event, Before, After, VictimCorporationId, SystemId, VictimTypeId);
}

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
