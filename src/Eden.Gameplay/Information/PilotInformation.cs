using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Information;

public sealed record ScanItem(int TypeId, int Quantity);

public sealed record ShipScan(long ShipId, double CapacitorCharge, double CapacitorCapacity, IReadOnlyList<ScanItem> Hardware);

public sealed record CargoScan(long TargetId, IReadOnlyList<ScanItem> Cargo);

public sealed record MiningScanResult(long AsteroidId, int TypeId, long Quantity);

public sealed record DirectionalScanContact(long Id, int TypeId, int GroupId, string Name, SpacePosition Position);

public sealed record InformationCycle(
    long PilotId,
    long ShipId,
    long TargetId,
    InformationModule Module,
    long StartedAt,
    long ReadyAt,
    long? TargetOwnerId = null
);

public sealed record InformationActivation(InformationCycle Cycle, double Capacitor);

public sealed record SalvageResult(bool Success, IReadOnlyList<InventoryChange> Cargo);

public sealed record InformationCompletion(ShipScan? Ship = null, CargoScan? Cargo = null, SalvageResult? Salvage = null);

public interface IPilotInformation
{
    InformationActivation Begin(long pilotId, long moduleId, DogmaEffect effect, long targetId, IReadOnlySet<long> locks, long now);
    InformationCompletion Complete(long pilotId, long moduleId, long expectedReadyAt, long now);
    void Stop(long pilotId, long moduleId, long expectedReadyAt);
    bool IsActive(InformationCycle cycle);
    IReadOnlyList<long> Hostiles(long pilotId, long now);
    void AdvanceTractor(InformationCycle cycle, long now);
    IReadOnlyList<MiningScanResult> Survey(long pilotId, long now);
    IReadOnlyList<DirectionalScanContact> Directional(long pilotId, double angle, double range, SpacePosition direction, long now);
}
