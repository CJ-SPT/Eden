using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Capsuleers;

public enum CapsuleerCareer
{
    Mining = 1,
    Hauling = 2,
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
    string? WaitingReason = null,
    long PaidUntil = 0,
    long ActiveTicks = 0,
    long DueTicks = 0,
    long ClockOffsetTicks = 0
);

public sealed record MinerObservation(DockedPilot Pilot, MinerProgress Work, InventoryItem? Laser, string? WaitingReason)
{
    public ShipHealth? Health { get; init; }
}

public interface ICapsuleerSimulation
{
    void ObserveAggression(long shipId);
}

public interface ICapsuleerScene
{
    int Stamp { get; }
    int MaterializedPilots => 0;
    void Apply(CapsuleerBatch batch);
    void Publish(MinerObservation observation);
    void Publish(HaulerObservation observation);
}
