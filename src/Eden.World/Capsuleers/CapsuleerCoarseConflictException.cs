namespace Eden.World.Capsuleers;

internal sealed class CapsuleerCoarseConflictException(int systemId)
    : InvalidOperationException("The coarse cursor changed before settlement.")
{
    internal int SystemId { get; } = systemId;
}
