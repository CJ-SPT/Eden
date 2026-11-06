using Eden.Gameplay.Combat;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class SavedCrimewatch
{
    public long PilotId { get; set; }
    public int SystemId { get; set; }
    public long WeaponsActive { get; set; }
    public long WeaponsUntil { get; set; }
    public long PvpActive { get; set; }
    public long PvpUntil { get; set; }
    public long NpcActive { get; set; }
    public long NpcUntil { get; set; }
    public long SuspectActive { get; set; }
    public long SuspectUntil { get; set; }
    public long CriminalActive { get; set; }
    public long CriminalUntil { get; set; }

    public CrimewatchTimers Timers() =>
        new(
            new(WeaponsActive, WeaponsUntil),
            new(PvpActive, PvpUntil),
            new(NpcActive, NpcUntil),
            new(SuspectActive, SuspectUntil),
            new(CriminalActive, CriminalUntil)
        );

    internal void Apply(CrimewatchTimers timers)
    {
        (WeaponsActive, WeaponsUntil) = (timers.Weapons.ActiveUntil, timers.Weapons.ExpiresAt);
        (PvpActive, PvpUntil) = (timers.Pvp.ActiveUntil, timers.Pvp.ExpiresAt);
        (NpcActive, NpcUntil) = (timers.Npc.ActiveUntil, timers.Npc.ExpiresAt);
        (SuspectActive, SuspectUntil) = (timers.Suspect.ActiveUntil, timers.Suspect.ExpiresAt);
        (CriminalActive, CriminalUntil) = (timers.Criminal.ActiveUntil, timers.Criminal.ExpiresAt);
    }
}

[PrimaryKey(nameof(LowerPilotId), nameof(HigherPilotId))]
public sealed class SavedEngagement
{
    public long LowerPilotId { get; set; }
    public long HigherPilotId { get; set; }
    public long ActiveUntil { get; set; }
    public long ExpiresAt { get; set; }
}

public sealed class SavedLogoff
{
    public long PilotId { get; set; }
    public long ShipId { get; set; }
    public int SystemId { get; set; }
    public long ExpiresAt { get; set; }
}

internal static class CrimewatchSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedCrimewatch>().ToTable("WorldCrimewatch").HasKey(s => s.PilotId);
        model.Entity<SavedCrimewatch>().HasIndex(s => s.SystemId);
        model.Entity<SavedEngagement>().ToTable("WorldEngagement");
        model.Entity<SavedEngagement>().HasIndex(s => s.HigherPilotId);
        model.Entity<SavedLogoff>().ToTable("WorldLogoff").HasKey(s => s.PilotId);
        model.Entity<SavedLogoff>().HasIndex(s => new { s.SystemId, s.ExpiresAt });
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCrimewatch(
                PilotId INTEGER PRIMARY KEY, SystemId INTEGER NOT NULL,
                WeaponsActive INTEGER NOT NULL, WeaponsUntil INTEGER NOT NULL,
                PvpActive INTEGER NOT NULL, PvpUntil INTEGER NOT NULL, NpcActive INTEGER NOT NULL, NpcUntil INTEGER NOT NULL,
                SuspectActive INTEGER NOT NULL, SuspectUntil INTEGER NOT NULL,
                CriminalActive INTEGER NOT NULL, CriminalUntil INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCrimewatch_SystemId ON WorldCrimewatch(SystemId);
            CREATE TABLE IF NOT EXISTS WorldEngagement(
                LowerPilotId INTEGER NOT NULL, HigherPilotId INTEGER NOT NULL, ActiveUntil INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL,
                PRIMARY KEY(LowerPilotId,HigherPilotId));
            CREATE INDEX IF NOT EXISTS IX_WorldEngagement_HigherPilotId ON WorldEngagement(HigherPilotId);
            CREATE TABLE IF NOT EXISTS WorldLogoff(
                PilotId INTEGER PRIMARY KEY, ShipId INTEGER NOT NULL, SystemId INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldLogoff_SystemId_ExpiresAt ON WorldLogoff(SystemId,ExpiresAt);
            """
        );
    }
}
