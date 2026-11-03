using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Skills;

public sealed record SkillInjection(IReadOnlyList<int> TypeIds, IReadOnlyList<InventoryChange> Changes);

public interface IPilotSkillBooks
{
    SkillInjection Inject(long pilotId, IReadOnlyList<long> itemIds);
}
