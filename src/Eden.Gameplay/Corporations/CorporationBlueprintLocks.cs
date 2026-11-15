namespace Eden.Gameplay.Corporations;

public sealed record CorporationBlueprintLock(long ItemId, int CorporationId, long OfficeId, int StationId, int TypeId, int FlagId);

public sealed record CorporationBlueprintLockChange(long ItemId, int CorporationId, int StationId, bool Locked);

public interface ICorporationBlueprintLocks
{
    IReadOnlyList<CorporationBlueprintLock> Read(long actorId, long? locationId = null);
}
