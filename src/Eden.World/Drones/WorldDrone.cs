using Eden.Gameplay.Drones;
using Eden.World.Combat;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Drones;

internal sealed class WorldDrone
{
    public long ItemId { get; set; }
    public long PilotId { get; set; }
    public long ControllerShipId { get; set; }
    public int CorporationId { get; set; }
    public int? SystemId { get; set; }
    public DroneActivity Activity { get; set; }
    public long TargetId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double VelocityX { get; set; }
    public double VelocityY { get; set; }
    public double VelocityZ { get; set; }
    public float SpeedLimit { get; set; }
    public bool Orbiting { get; set; }
    public double Shield { get; set; }
    public double ArmorDamage { get; set; }
    public double StructureDamage { get; set; }
    public long UpdatedAt { get; set; }
    public long ReadyAt { get; set; }
    public long CommandedAt { get; set; }
    public long ShotAt { get; set; }
    public long ShotTargetId { get; set; }
}

internal static class DroneSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<WorldDroneSettings>().ToTable("WorldDroneSettings").HasKey(d => d.PilotId);
        model.Entity<WorldDrone>().ToTable("WorldDrone").HasKey(d => d.ItemId);
        model.Entity<WorldDrone>().HasOne<OwnedItem>().WithMany().HasForeignKey(d => d.ItemId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<WorldDrone>().HasIndex(d => new { d.PilotId, d.ControllerShipId });
        model.Entity<WorldDrone>().HasIndex(d => d.SystemId);
    }

    internal static void Upgrade(WorldDbContext world)
    {
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldNpcAggression')")
                .AsEnumerable()
                .Contains(nameof(NpcAggression.LastTargetId))
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldNpcAggression ADD COLUMN LastTargetId INTEGER");
        }
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldDrone(
                ItemId INTEGER PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                PilotId INTEGER NOT NULL,ControllerShipId INTEGER NOT NULL,CorporationId INTEGER NOT NULL,SystemId INTEGER,
                Activity INTEGER NOT NULL,TargetId INTEGER NOT NULL,
                X REAL NOT NULL,Y REAL NOT NULL,Z REAL NOT NULL,
                VelocityX REAL NOT NULL,VelocityY REAL NOT NULL,VelocityZ REAL NOT NULL,
                SpeedLimit REAL NOT NULL,Orbiting INTEGER NOT NULL,
                Shield REAL NOT NULL,ArmorDamage REAL NOT NULL,StructureDamage REAL NOT NULL,
                UpdatedAt INTEGER NOT NULL,ReadyAt INTEGER NOT NULL,CommandedAt INTEGER NOT NULL,
                ShotAt INTEGER NOT NULL,ShotTargetId INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldDrone_PilotId_ControllerShipId ON WorldDrone(PilotId,ControllerShipId);
            CREATE INDEX IF NOT EXISTS IX_WorldDrone_SystemId ON WorldDrone(SystemId);
            CREATE TABLE IF NOT EXISTS WorldDroneSettings(
                PilotId INTEGER PRIMARY KEY, Aggressive INTEGER NOT NULL,FocusFire INTEGER NOT NULL);
            """
        );
    }
}

internal sealed class WorldDroneSettings
{
    public long PilotId { get; set; }
    public bool Aggressive { get; set; } = true;
    public bool FocusFire { get; set; }
}
