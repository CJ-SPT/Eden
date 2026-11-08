using Eden.Gameplay.Capsuleers;

namespace Eden.Data.World.Capsuleers;

public sealed class SavedCapsuleerProvisioning
{
    public int Id { get; set; }
    public long WorldSeed { get; set; }
}

public sealed class SavedCapsuleerSystem
{
    public int SystemId { get; set; }
    public CapsuleerEligibility Eligibility { get; set; }
    public int? HomeStationId { get; set; }
    public int? DestinationStationId { get; set; }
    public string CatalogFingerprint { get; set; } = "";
    public int PolicyVersion { get; set; }
}
