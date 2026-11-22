using Eden.Gameplay.Ammunition;

namespace Eden.Gameplay.Repair;

public enum DeveloperRepairKind
{
    Ship,
    Capacitor,
    FittedModules,
    OwnedModule,
}

public sealed record DeveloperRepairRequest(DeveloperRepairKind Kind, long? Item = null);

public sealed record DeveloperRepairResult(IReadOnlyList<ModuleRepair> Modules, IReadOnlyList<MagazineChange> Charges);

public interface IPilotDeveloperRepairs
{
    DeveloperRepairResult Restore(long pilot, long ship, DeveloperRepairRequest request);
}
