using Eden.Protocol.Docking;

namespace Eden.Protocol.Skills;

public sealed record SkillInjection(IReadOnlyList<int> TypeIds, IReadOnlyList<InventoryChange> Changes);

public interface IPilotSkillBooks
{
    SkillInjection Inject(long pilotId, IReadOnlyList<long> itemIds);
}
