namespace Eden.Gameplay.Combat;

public static class NpcTagLootRules
{
    // Eden's explicit acquisition policy: 25% per ordinary pirate, one tag;
    // each hull class supplies two adjacent grades. CCP's hidden drop tables are not recovered.
    public const int DropOutcomes = 4;
    public const int GradeCount = 10;

    public static int? Grade(NpcHullClass hull, long seed)
    {
        int pair = hull switch
        {
            NpcHullClass.Frigate => 0,
            NpcHullClass.Destroyer => 1,
            NpcHullClass.Cruiser => 2,
            NpcHullClass.Battlecruiser => 3,
            NpcHullClass.Battleship => 4,
            _ => throw new InvalidDataException("Unsupported pirate hull for criminal tags."),
        };
        ulong roll = unchecked((ulong)seed);
        return roll % DropOutcomes == 0 ? pair * 2 + (int)(roll / DropOutcomes % 2) : null;
    }
}
