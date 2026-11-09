using Eden.Gameplay.Capsuleers;

namespace Eden.Data.Catalog.Capsuleers;

public sealed record TrafficEquipment(int TypeId, int Flag, int Quantity);

public sealed record TrafficProfile(
    long CharacterId,
    string Name,
    long KillId,
    DateTimeOffset KilledAt,
    int SystemId,
    int RegionId,
    TrafficSecurityBand SecurityBand,
    int HullTypeId,
    TrafficEquipment[] Equipment
);

public sealed record TrafficArchive(string Date, string Sha256, long Bytes);

public sealed record TrafficFitCache(string Fingerprint, Dictionary<string, string> Decisions);

public sealed record TrafficSnapshotManifest(
    int Version,
    string Identity,
    string CatalogFingerprint,
    string From,
    string Through,
    TrafficArchive[] Archives,
    long Records,
    long Duplicates,
    int Accepted,
    int Pilots,
    int Hulls,
    int Fits,
    Dictionary<string, long> Rejected
);
