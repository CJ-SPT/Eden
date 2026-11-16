using Eden.Data.World.Alliances;
using Eden.Gameplay.Alliances;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Alliances;

[Injectable(InjectionType.Singleton)]
internal sealed partial class AllianceStore(WorldDatabase database, TimeProvider clock) : IAlliances
{
    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Alliances require an active resident world.");

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public Alliance? Read(int allianceId) => Resident.Economy.Inspect(world => Snapshot(world, allianceId));

    public int? Affiliation(int corporationId) =>
        LiveWorld.Find(database.Path)?.Economy.Inspect(world => world.Set<SavedAllianceMember>().Find(corporationId)?.AllianceId);

    public IReadOnlyList<Alliance> List() =>
        Resident.Economy.Inspect(world =>
            (IReadOnlyList<Alliance>)world.Set<SavedAlliance>().OrderBy(row => row.Name).Select(row => Snapshot(world, row.Id)!).ToArray()
        );

    public IReadOnlyList<AllianceMember> Members(int allianceId) =>
        Resident.Economy.Inspect(world =>
            (IReadOnlyList<AllianceMember>)
                world
                    .Filter<SavedAllianceMember>(row => row.AllianceId == allianceId)
                    .OrderBy(row => row.CorporationId)
                    .Select(row => new AllianceMember(row.CorporationId, row.AllianceId, row.JoinedAt, row.ChosenExecutorId))
                    .ToArray()
        );

    internal static Alliance? Snapshot(WorldAction world, int allianceId)
    {
        var row = world.Set<SavedAlliance>().Find(allianceId);
        if (row is null)
        {
            return null;
        }
        int[] corporations = world
            .Filter<SavedAllianceMember>(member => member.AllianceId == allianceId)
            .Select(m => m.CorporationId)
            .ToArray();
        return new(
            row.Id,
            new(row.Name, row.Ticker, row.Description, row.Url),
            row.CreatorId,
            row.CreatorCorporationId,
            row.ExecutorCorporationId,
            row.CreatedAt,
            row.ClosedAt,
            corporations.Length,
            corporations.Sum(corporation => world.Pilots.Count(pilot => pilot.CorporationId == corporation))
        );
    }
}
