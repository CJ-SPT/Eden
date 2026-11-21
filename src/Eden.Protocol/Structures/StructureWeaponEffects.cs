using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Flight;

namespace Eden.Protocol.Structures;

internal sealed record StructureBeam(long SourceId, StructureWeaponCycle Cycle)
{
    // Targeted Standup capacitor and tackle effects use the Laser GUID in build 3662168.
    internal DestinyCommand Command(bool active) =>
        new DestinyCommand.TimedSpecialEffect(
            SourceId,
            Cycle.ModuleId,
            Cycle.TypeId,
            Cycle.TargetId,
            null,
            "effects.Laser",
            true,
            active,
            active,
            Cycle.DurationMilliseconds,
            Cycle.StartedAt
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
