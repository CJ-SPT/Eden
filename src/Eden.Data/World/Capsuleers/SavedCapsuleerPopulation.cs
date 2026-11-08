using Eden.Gameplay.Capsuleers;

namespace Eden.Data.World.Capsuleers;

public sealed class SavedCapsuleerPopulation
{
    public int SystemId { get; set; }
    public int HomeStationId { get; set; }
    public long Seed { get; set; }
    public int Version { get; set; }
}

public sealed class SavedCapsuleerAgent
{
    public long PilotId { get; set; }
    public int CommunitySystemId { get; set; }
    public CapsuleerCareer Career { get; set; }
    public long FounderShipId { get; set; }
    public long FounderModuleId { get; set; }
    public long StartingCents { get; set; }
}

public sealed class SavedCapsuleerJob
{
    public long PilotId { get; set; }
    public long Revision { get; set; }
    public long Sequence { get; set; }
    public long DueTicks { get; set; }
    public CapsuleerAction Action { get; set; }
}

public sealed class SavedCapsuleerClock
{
    public int Id { get; set; }
    public long ElapsedTicks { get; set; }
}
