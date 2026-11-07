namespace Eden.World.Docking;

public sealed class FlightPosition
{
    public long ShipId { get; set; }
    public int? SystemId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
