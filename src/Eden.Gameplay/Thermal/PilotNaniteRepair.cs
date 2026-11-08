using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Thermal;

public sealed record NaniteRepairKey(long ModuleId, long StartedAt, long CompletesAt, Guid Generation);

public sealed record NaniteRepairSchedule(
    NaniteRepairKey Key,
    long PilotId,
    long ShipId,
    double InitialDamage,
    double InitialHitPoints,
    long PasteUnits
);

public sealed record NaniteRepairStarted(long ModuleId, NaniteRepairSchedule? Schedule, ModuleCondition Condition);

public sealed record NaniteRepairBatch(
    IReadOnlyList<NaniteRepairStarted> Started,
    IReadOnlyList<long> MissingPaste,
    IReadOnlyList<long> Failed,
    IReadOnlyList<InventoryChange> Inventory
);

public interface IPilotNaniteRepair
{
    IReadOnlyList<NaniteRepairSchedule> Read(long pilotId, long shipId, long now);
    NaniteRepairBatch Start(long pilotId, long shipId, IReadOnlyList<long> modules, long now);
    ModuleCondition? Stop(long pilotId, long shipId, NaniteRepairKey expected, long now);
    IReadOnlyList<ModuleCondition> Cancel(long pilotId, long shipId, long now);
}
