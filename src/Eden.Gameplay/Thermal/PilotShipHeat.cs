namespace Eden.Gameplay.Thermal;

public interface IPilotShipHeat
{
    ShipHeatSnapshot Read(long pilotId, long shipId, long now);
}
