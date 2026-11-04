namespace Eden.Gameplay.Docking;

public sealed record ShipDirtState(long ShipId, long Timestamp);

public interface IPilotShipAppearance
{
    ShipDirtState Read(long pilotId, long shipId);
    ShipDirtState Clean(long pilotId, long shipId);
}

public static class ShipDirtRules
{
    public static long CleanTimestamp(long? previous, long now)
    {
        if (now <= 0)
        {
            throw new InvalidDataException("A positive native ship timestamp is required.");
        }
        return Math.Max(previous ?? now, now);
    }
}
