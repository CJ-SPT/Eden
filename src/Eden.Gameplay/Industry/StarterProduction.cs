namespace Eden.Gameplay.Industry;

// Build 3662168 starter roles: a gun frigate, ore miner and ordinary cargo hauler.
public enum StarterProductionType
{
    Merlin = 603,
    Venture = 32880,
    Badger = 648,
    ExpandedCargohold = 1317,
    DamageControl = 2046,
}

public static class StarterProduction
{
    public static bool IsHull(int typeId) =>
        typeId is (int)StarterProductionType.Merlin or (int)StarterProductionType.Venture or (int)StarterProductionType.Badger;

    public static bool IsUtility(int typeId) =>
        typeId is (int)StarterProductionType.ExpandedCargohold or (int)StarterProductionType.DamageControl;
}
