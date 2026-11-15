using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationBlueprintLockStore(WorldDatabase database, ICorporationVoting voting) : ICorporationBlueprintLocks
{
    public IReadOnlyList<CorporationBlueprintLock> Read(long actorId, long? locationId = null)
    {
        voting.Advance();
        var resident = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Blueprint locks require an active resident world.");
        return resident.Economy.Inspect<IReadOnlyList<CorporationBlueprintLock>>(world =>
        {
            var pilot = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown pilot.");
            var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId);
            if (corporation is null)
            {
                return [];
            }
            var access = CorporationRoleStore.Effective(world, actorId, corporation);
            return world
                .Set<SavedCorporationBlueprintLock>()
                .Where(row => row.CorporationId == corporation.Id)
                .AsEnumerable()
                .Where(row =>
                    (locationId is null || row.StationId == locationId || row.OfficeId == locationId)
                    && CorporationStorageRules.CanQuery(access, row.StationId, corporation.StationId, row.FlagId)
                )
                .OrderBy(row => row.ItemId)
                .Select(row => new CorporationBlueprintLock(
                    row.ItemId,
                    row.CorporationId,
                    row.OfficeId,
                    row.StationId,
                    row.TypeId,
                    row.FlagId
                ))
                .ToArray();
        });
    }
}
