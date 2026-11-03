namespace Eden.Gameplay.Combat;

// Pinned crimewatch.const shipSafetyLevelNone/Partial/Full values.
public enum ShipSafetyLevel
{
    Disabled = 0,
    Partial = 1,
    Full = 2,
}

public interface IPilotSafety
{
    ShipSafetyLevel Read(long pilotId);
    void Set(long pilotId, ShipSafetyLevel level);
}
