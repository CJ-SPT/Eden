using Eden.Gameplay.Drones;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.Drones;

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

internal sealed class WorldDroneSettings
{
    public long PilotId { get; set; }
    public bool Aggressive { get; set; } = true;
    public bool FocusFire { get; set; }
}
