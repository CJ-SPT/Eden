using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;

namespace Eden.Protocol.Structures;

[Injectable(InjectionType.Singleton)]
internal sealed class SharedStructureControl(IPilotStructureControl authority)
{
    internal long? Pilot(long structureId) => authority.Pilot(structureId);

    internal IStructureControlLease Acquire(long structureId, long pilotId, long sessionId) =>
        authority.Acquire(structureId, pilotId, sessionId);
}
