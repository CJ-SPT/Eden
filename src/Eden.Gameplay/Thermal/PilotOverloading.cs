namespace Eden.Gameplay.Thermal;

public enum OverloadScope
{
    Module,
    Rack,
}

public sealed record ModuleOverloadCommand(
    long PilotId,
    long ShipId,
    long ModuleId,
    int? EffectId,
    OverloadScope Scope,
    bool Enabled,
    long Time
);

public sealed record ModuleOverloadState(long ModuleId, int EffectId, bool Requested, bool Pending);

public interface IPilotOverloading
{
    IReadOnlyList<ModuleOverloadState> Set(ModuleOverloadCommand command);
    IReadOnlySet<long> Requested(long pilot, long ship);
    void Clear(long pilot, long ship);
}
