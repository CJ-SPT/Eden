namespace Eden.Gameplay.Structures;

public sealed record StructureControlToken(long StructureId, long PilotId, long SessionId, Guid LeaseId);

public interface IStructureControlLease : IDisposable
{
    StructureControlToken Token { get; }
    long StructureId => Token.StructureId;
    long PilotId => Token.PilotId;
    long SessionId => Token.SessionId;
}

public interface IPilotStructureControl
{
    IStructureControlLease Acquire(long structureId, long pilotId, long sessionId);
    long? Pilot(long structureId);
    StructureSnapshot Require(StructureControlToken token);
}
