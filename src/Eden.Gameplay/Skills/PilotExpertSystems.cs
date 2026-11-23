using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Skills;

public sealed record InstalledExpertSystem(int TypeId, long InstalledAt, long ExpiresAt);

public sealed record ExpertVirtualSkill(int TypeId, int Level, double Rank);

public sealed record ExpertSystemState(IReadOnlyList<InstalledExpertSystem> Systems, IReadOnlyList<ExpertVirtualSkill> Skills);

public sealed record ExpertSystemEvent(int TypeId, bool Added);

public sealed record ExpertSystemChange(
    ExpertSystemState Before,
    ExpertSystemState After,
    InventoryChange? Inventory,
    IReadOnlyList<ExpertSystemEvent> Events
);

public interface IPilotExpertSystems
{
    ExpertSystemState Read(long pilotId);
    ExpertSystemChange Consume(long pilotId, long itemId);
    ExpertSystemChange Remove(long pilotId, int typeId);
    ExpertSystemChange? Expire(long pilotId);
}

public static class ExpertSystemRules
{
    // Eve.js 261108.1 expertSystemRuntime installation and top-up limits.
    public const int MaximumActive = 3;
    public const long TopUpWindow = 30 * TimeSpan.TicksPerDay;
}
