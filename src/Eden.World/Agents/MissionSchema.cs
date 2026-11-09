using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Agents;

internal static class MissionSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedMission>().ToTable("WorldMission").HasKey(m => m.Id);
        model.Entity<SavedMission>().HasOne<Pilot>().WithMany().HasForeignKey(m => m.PilotId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<SavedMission>().HasIndex(m => new { m.PilotId, m.AgentId }).IsUnique().HasFilter("ClosedAt IS NULL");
        model.Entity<SavedMission>().HasIndex(m => new { m.SystemId, m.State });
        model.Entity<SavedMissionMember>().ToTable("WorldMissionMember").HasKey(m => m.NpcId);
        model
            .Entity<SavedMissionMember>()
            .HasOne<SavedMission>()
            .WithMany()
            .HasForeignKey(m => m.MissionId)
            .OnDelete(DeleteBehavior.Cascade);
        model.Entity<SavedMissionMember>().HasIndex(m => new { m.MissionId, m.Wave });
        model
            .Entity<SavedMissionProgress>()
            .ToTable("WorldMissionProgress")
            .HasKey(m => new
            {
                m.PilotId,
                m.FactionId,
                m.Level,
            });
        model.Entity<SavedMissionProgress>().HasOne<Pilot>().WithMany().HasForeignKey(m => m.PilotId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<SavedAgentDecline>().ToTable("WorldAgentDecline").HasKey(m => new { m.PilotId, m.AgentId });
        model.Entity<SavedAgentDecline>().HasOne<Pilot>().WithMany().HasForeignKey(m => m.PilotId).OnDelete(DeleteBehavior.Cascade);
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldMission (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                AgentId INTEGER NOT NULL, MissionId INTEGER NOT NULL, Level INTEGER NOT NULL, State INTEGER NOT NULL,
                Storyline INTEGER NOT NULL, OfferedAt INTEGER NOT NULL, AcceptedAt INTEGER, ExpiresAt INTEGER NOT NULL,
                ClosedAt INTEGER, DestinationStation INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, Wave INTEGER NOT NULL, ObjectiveComplete INTEGER NOT NULL,
                IskCents INTEGER NOT NULL, LoyaltyPoints INTEGER NOT NULL, CorporationFraction REAL NOT NULL,
                FactionFraction REAL NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldMission_PilotId_AgentId ON WorldMission(PilotId,AgentId) WHERE ClosedAt IS NULL;
            CREATE INDEX IF NOT EXISTS IX_WorldMission_SystemId_State ON WorldMission(SystemId,State);
            CREATE TABLE IF NOT EXISTS WorldMissionMember (
                NpcId INTEGER PRIMARY KEY, MissionId INTEGER NOT NULL REFERENCES WorldMission(Id) ON DELETE CASCADE,
                Wave INTEGER NOT NULL, Trigger INTEGER NOT NULL, CompletesMission INTEGER NOT NULL, Required INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldMissionMember_MissionId_Wave ON WorldMissionMember(MissionId,Wave);
            CREATE TABLE IF NOT EXISTS WorldMissionProgress (
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE, FactionId INTEGER NOT NULL,
                Level INTEGER NOT NULL, Completed INTEGER NOT NULL, PRIMARY KEY(PilotId,FactionId,Level));
            CREATE TABLE IF NOT EXISTS WorldAgentDecline (
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE, AgentId INTEGER NOT NULL,
                LastFreeDeclineAt INTEGER NOT NULL, PRIMARY KEY(PilotId,AgentId));
            """
        );
    }
}
