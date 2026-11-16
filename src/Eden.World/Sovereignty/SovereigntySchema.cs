using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Sovereignty;

internal static class SovereigntySchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedSovereigntyClaim>().ToTable("WorldSovereigntyClaim").HasKey(row => row.SystemId);
        model.Entity<SavedSovereigntyClaim>().Property(row => row.SystemId).ValueGeneratedNever();
        model.Entity<SavedSovereigntyClaim>().HasIndex(row => row.HubId).IsUnique();
        model.Entity<SavedSovereigntyClaim>().HasIndex(row => row.AllianceId);
        model.Entity<SavedSovereigntyClaim>().HasOne<SavedAlliance>().WithMany().HasForeignKey(row => row.AllianceId);
        model.Entity<SavedSovereigntyClaim>().HasOne<SavedCorporation>().WithMany().HasForeignKey(row => row.CorporationId);
        model.Entity<SavedSovereigntyHub>().ToTable("WorldSovereigntyHub").HasKey(row => row.SystemId);
        model.Entity<SavedSovereigntyHub>().Property(row => row.SystemId).ValueGeneratedNever();
        model.Entity<SavedSovereigntyHub>().HasIndex(row => row.HubId).IsUnique();
        model.Entity<SavedSovereigntyHub>().HasOne<SavedStructure>().WithMany().HasForeignKey(row => row.HubId);
        model.Entity<SavedEntosisCycle>().ToTable("WorldEntosisCycle").HasKey(row => row.ModuleId);
        model.Entity<SavedEntosisCycle>().Property(row => row.ModuleId).ValueGeneratedNever();
        model.Entity<SavedEntosisCycle>().HasIndex(row => row.ShipId);
        model.Entity<SavedEntosisCycle>().HasIndex(row => row.PilotId);
        model.Entity<SavedEntosisCycle>().HasIndex(row => row.SystemId);
        model.Entity<SavedEntosisCycle>().HasIndex(row => row.HubId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldSovereigntyClaim(
                SystemId INTEGER PRIMARY KEY, HubId INTEGER NOT NULL,
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id),
                AllianceId INTEGER NOT NULL REFERENCES WorldAlliance(Id), ClaimedAt INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldSovereigntyClaim_HubId ON WorldSovereigntyClaim(HubId);
            CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyClaim_AllianceId ON WorldSovereigntyClaim(AllianceId);
            """
        );

    internal static void UpgradeCapture(WorldDbContext world, int version)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldSovereigntyHub(
                SystemId INTEGER PRIMARY KEY, HubId INTEGER NOT NULL REFERENCES WorldStructure(Id),
                CaptureDuration INTEGER NOT NULL, ControllingAllianceId INTEGER, ProgressTicks INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL, ScoringAllianceId INTEGER, Rate INTEGER NOT NULL, Blocked INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldSovereigntyHub_HubId ON WorldSovereigntyHub(HubId);
            CREATE TABLE IF NOT EXISTS WorldEntosisCycle(
                ModuleId INTEGER PRIMARY KEY, TypeId INTEGER NOT NULL, PilotId INTEGER NOT NULL, ShipId INTEGER NOT NULL,
                HubId INTEGER NOT NULL, SystemId INTEGER NOT NULL, CorporationId INTEGER NOT NULL, AllianceId INTEGER NOT NULL,
                StartedAt INTEGER NOT NULL, EndsAt INTEGER NOT NULL, WarmupUntil INTEGER NOT NULL, Range REAL NOT NULL,
                SpeedLimit REAL NOT NULL, Capital INTEGER NOT NULL, Repeats INTEGER NOT NULL,
                Stopping INTEGER NOT NULL, Contributing INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldEntosisCycle_ShipId ON WorldEntosisCycle(ShipId);
            CREATE INDEX IF NOT EXISTS IX_WorldEntosisCycle_PilotId ON WorldEntosisCycle(PilotId);
            CREATE INDEX IF NOT EXISTS IX_WorldEntosisCycle_SystemId ON WorldEntosisCycle(SystemId);
            CREATE INDEX IF NOT EXISTS IX_WorldEntosisCycle_HubId ON WorldEntosisCycle(HubId);
            """
        );
        if (version < 180)
        {
            // Old generic Upwell hubs retain their identity and owning corporation
            // as recoverable assets. They never turn into free territorial claims.
            world.Database.ExecuteSql(
                $"""
                UPDATE WorldStructure SET State={(int)StructureState.Unanchored}, StateEndsAt=NULL, UnanchorAt=NULL
                WHERE TypeId={EntosisRules.HubTypeId} AND State<>{(int)StructureState.Destroyed}
                    AND Id NOT IN (SELECT HubId FROM WorldSovereigntyClaim)
                """
            );
        }
    }
}
