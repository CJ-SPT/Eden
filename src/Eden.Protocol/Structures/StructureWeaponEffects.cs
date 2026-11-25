using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

internal sealed record StructureBeam(long SourceId, StructureWeaponCycle Cycle, StructurePointDefensePulse? Pulse = null)
{
    // Targeted Standup capacitor and tackle effects use the Laser GUID in build 3662168.
    internal DestinyCommand Command(bool active) =>
        new DestinyCommand.TimedSpecialEffect(
            SourceId,
            Cycle.ModuleId,
            Cycle.TypeId,
            Pulse is null ? Cycle.TargetId : null,
            Pulse?.Magazine.TypeId,
            Cycle.Effect == DogmaEffect.StructurePointDefense ? "effects.PointDefense" : "effects.Laser",
            true,
            active,
            active,
            Cycle.DurationMilliseconds,
            Cycle.StartedAt,
            Pulse is null
                ? null
                : new BlueInstance(
                    "utillib.KeyVal",
                    new Dictionary<string, object?>
                    {
                        ["candidateTypeListId"] = -1,
                        ["hitRange"] = Pulse.Range,
                        ["trackTargets"] = false,
                    }
                )
        );
}

[Injectable(InjectionType.Singleton)]
internal sealed class StructureWeaponEffects
{
    private readonly Dictionary<int, StructureBeam[]> _systems = [];

    internal void Set(int system, StructureBeam beam, bool active)
    {
        lock (_systems)
        {
            var next = _systems
                .GetValueOrDefault(system, [])
                .Where(old => active ? old.Cycle.ModuleId != beam.Cycle.ModuleId : old != beam)
                .Concat(active ? [beam] : Array.Empty<StructureBeam>())
                .ToArray();
            if (next.Length == 0)
            {
                _systems.Remove(system);
            }
            else
            {
                _systems[system] = next;
            }
        }
    }

    internal IReadOnlyList<StructureBeam> Read(int system)
    {
        lock (_systems)
        {
            return _systems.GetValueOrDefault(system) ?? Array.Empty<StructureBeam>();
        }
    }
}
