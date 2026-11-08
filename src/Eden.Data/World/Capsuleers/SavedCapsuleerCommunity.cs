using Eden.Gameplay.Capsuleers;

namespace Eden.Data.World.Capsuleers;

public sealed class SavedCapsuleerCommunity
{
    public int Id { get; set; }
    public int Seed { get; set; }
    public int SchoolId { get; set; }
    public CapsuleerCareer Career { get; set; }
    public long? PilotId { get; set; }
    public int HomeStationId { get; set; }
    public long FounderShipId { get; set; }
    public long FounderModuleId { get; set; }
    public long StartingCents { get; set; }
    public string? Progress { get; set; }
}
