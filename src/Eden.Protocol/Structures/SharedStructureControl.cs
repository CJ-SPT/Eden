using Eden.Infrastructure.Registration;

namespace Eden.Protocol.Structures;

[Injectable(InjectionType.Singleton)]
internal sealed class SharedStructureControl
{
    private readonly object _gate = new();
    private readonly Dictionary<long, StructureControlLease> _controllers = [];

    internal long? Pilot(long structureId)
    {
        lock (_gate)
        {
            return _controllers.GetValueOrDefault(structureId)?.PilotId;
        }
    }

    internal StructureControlLease Acquire(long structureId, long pilotId, long sessionId)
    {
        lock (_gate)
        {
            if (_controllers.TryGetValue(structureId, out var current))
            {
                if (current.SessionId == sessionId && current.PilotId == pilotId)
                {
                    return current;
                }
                throw new InvalidDataException("Another pilot is controlling this structure.");
            }
            var lease = new StructureControlLease(structureId, pilotId, sessionId, this);
            _controllers.Add(structureId, lease);
            return lease;
        }
    }

    internal void Release(StructureControlLease lease)
    {
        lock (_gate)
        {
            if (_controllers.GetValueOrDefault(lease.StructureId) == lease)
            {
                _controllers.Remove(lease.StructureId);
            }
        }
    }
}

internal sealed class StructureControlLease(long structureId, long pilotId, long sessionId, SharedStructureControl owner) : IDisposable
{
    internal long StructureId { get; } = structureId;
    internal long PilotId { get; } = pilotId;
    internal long SessionId { get; } = sessionId;

    public void Dispose() => owner.Release(this);
}
