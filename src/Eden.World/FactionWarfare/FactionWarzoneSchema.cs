using Eden.Data.World.Characters;
using Eden.Data.World.FactionWarfare;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.FactionWarfare;

internal static class FactionWarzoneSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedWarzoneOccupation>().ToTable("WorldWarzoneOccupation").HasKey(row => row.SystemId);
        model.Entity<SavedWarzoneOccupation>().Property(row => row.SystemId).ValueGeneratedNever();
        model.Entity<SavedMilitiaEnlistment>().ToTable("WorldMilitiaEnlistment").HasKey(row => row.PilotId);
        model.Entity<SavedMilitiaEnlistment>().Property(row => row.PilotId).ValueGeneratedNever();
        model.Entity<SavedMilitiaEnlistment>().HasOne<Pilot>().WithMany().HasForeignKey(row => row.PilotId);
        model.Entity<SavedMilitiaOrganization>().ToTable("WorldMilitiaOrganization").HasKey(row => row.OwnerId);
        model.Entity<SavedMilitiaOrganization>().Property(row => row.OwnerId).ValueGeneratedNever();
        model.Entity<SavedMilitiaStandingWarning>().ToTable("WorldMilitiaStandingWarning").HasKey(row => row.PilotId);
        model.Entity<SavedMilitiaStandingWarning>().Property(row => row.PilotId).ValueGeneratedNever();
        model.Entity<SavedMilitiaStandingWarning>().HasOne<Pilot>().WithMany().HasForeignKey(row => row.PilotId);
        model.Entity<SavedMilitiaStandingWarning>().HasIndex(row => row.OwnerId);
        model.Entity<SavedMilitiaNavyPursuit>().ToTable("WorldMilitiaNavyPursuit").HasKey(row => row.PilotId);
        model.Entity<SavedMilitiaNavyPursuit>().Property(row => row.PilotId).ValueGeneratedNever();
        model.Entity<SavedMilitiaNavyPursuit>().HasOne<Pilot>().WithMany().HasForeignKey(row => row.PilotId);
        model.Entity<SavedMilitiaComplex>().ToTable("WorldMilitiaComplex").HasKey(row => row.Slot);
        model.Entity<SavedMilitiaComplex>().Property(row => row.Slot).ValueGeneratedNever();
        model.Entity<SavedMilitiaHub>().ToTable("WorldMilitiaHub").HasKey(row => row.SystemId);
        model.Entity<SavedMilitiaHub>().Property(row => row.SystemId).ValueGeneratedNever();
        model.Entity<SavedMilitiaHubContribution>().ToTable("WorldMilitiaHubContribution").HasKey(row => new { row.NpcId, row.PilotId });
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldWarzoneOccupation(
                SystemId INTEGER PRIMARY KEY,
                Occupier INTEGER NOT NULL,
                VictoryPoints INTEGER NOT NULL,
                CapturedAt INTEGER);
            CREATE TABLE IF NOT EXISTS WorldMilitiaEnlistment(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                DirectFaction INTEGER,
                DirectCooldownUntil INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldMilitiaOrganization(
                OwnerId INTEGER PRIMARY KEY,
                Kind INTEGER NOT NULL,
                Faction INTEGER NOT NULL,
                Phase INTEGER NOT NULL,
                EffectiveAt INTEGER NOT NULL,
                StartedAt INTEGER,
                ReviewedAt INTEGER NOT NULL,
                StandingExpiry INTEGER);
            CREATE TABLE IF NOT EXISTS WorldMilitiaStandingWarning(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                OwnerId INTEGER NOT NULL,
                CorporationId INTEGER NOT NULL,
                Faction INTEGER NOT NULL,
                ExpiresAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldMilitiaStandingWarning_OwnerId ON WorldMilitiaStandingWarning(OwnerId);
            CREATE TABLE IF NOT EXISTS WorldMilitiaNavyPursuit(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ShipId INTEGER NOT NULL,
                SystemId INTEGER NOT NULL,
                Faction INTEGER NOT NULL,
                RespondAt INTEGER NOT NULL,
                FrigateId INTEGER,
                CruiserId INTEGER);
            CREATE TABLE IF NOT EXISTS WorldMilitiaComplex(
                Slot INTEGER PRIMARY KEY,
                Generation INTEGER NOT NULL,
                Occupier INTEGER NOT NULL,
                Progress INTEGER NOT NULL,
                ReplaceAt INTEGER,
                DefenderId INTEGER);
            CREATE TABLE IF NOT EXISTS WorldMilitiaHub(
                SystemId INTEGER PRIMARY KEY,
                NpcId INTEGER NOT NULL,
                Attacker INTEGER,
                TransferAt INTEGER);
            CREATE TABLE IF NOT EXISTS WorldMilitiaHubContribution(
                NpcId INTEGER NOT NULL,
                PilotId INTEGER NOT NULL,
                Damage REAL NOT NULL,
                PRIMARY KEY(NpcId, PilotId));
            """
        );
}
