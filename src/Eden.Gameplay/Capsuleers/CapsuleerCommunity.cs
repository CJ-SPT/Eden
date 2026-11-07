using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Capsuleers;

public enum CapsuleerCareer
{
    Mining = 1,
}

public sealed record CapsuleerIdentity(long PilotId, int Seed, int SchoolId, CapsuleerCareer Career);

public interface ICapsuleerCommunity
{
    IReadOnlyList<CapsuleerIdentity> Pilots();
}

public enum MinerActivity
{
    Docked,
    FlyingToBelt,
    ApproachingOre,
    Locking,
    Mining,
    Returning,
    Waiting,
    ReadyToReturn,
}

public sealed record MinerProgress(
    MinerActivity Activity,
    long TargetId,
    int Stamp,
    int DueStamp,
    int CycleSeconds,
    long ModuleId,
    int Cycles,
    NavigationFrame Navigation,
    long ShipId = 0,
    int Trips = 0,
    string? WaitingReason = null
);

public sealed record MinerObservation(DockedPilot Pilot, MinerProgress Work, InventoryItem? Laser, string? WaitingReason);

public interface ICapsuleerSimulation
{
    MinerObservation? Advance();
}

public interface ICapsuleerScene
{
    void Publish(MinerObservation observation);
}
