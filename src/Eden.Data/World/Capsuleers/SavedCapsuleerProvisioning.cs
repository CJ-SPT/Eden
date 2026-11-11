using Eden.Gameplay.Capsuleers;

namespace Eden.Data.World.Capsuleers;

public sealed class SavedCapsuleerSimulation
{
    public int SystemId { get; set; }
    public long StartedTicks { get; set; }
}

public sealed class SavedCapsuleerCoarseWork
{
    public int SystemId { get; set; }
    public long Generation { get; set; }
    public long Cursor { get; set; }
    public long SettledTicks { get; set; }
    public long NextTicks { get; set; }
}

public sealed class SavedCapsuleerAdmission
{
    public long PilotId { get; set; }
}

public sealed class SavedCapsuleerCoarsePilot
{
    public long PilotId { get; set; }
    public long SettledTicks { get; set; }
}

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
