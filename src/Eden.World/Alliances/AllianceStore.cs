using Eden.Data.World.Alliances;
using Eden.Gameplay.Alliances;
using Eden.Infrastructure.Registration;
using Eden.World.Mail;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;
using Eden.World.Storage;

namespace Eden.World.Alliances;

[Injectable(InjectionType.Singleton)]
internal sealed partial class AllianceStore(WorldDatabase database, TimeProvider clock, InboxChangeSignal inbox, PilotEntosisStore entosis)
    : IAlliances,
        IAllianceMembership,
        IAllianceBilling,
        IAllianceSocial,
        IAllianceCalendar
{
    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Alliances require an active resident world.");

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public Alliance? Read(int allianceId) => Resident.Economy.Inspect(world => Snapshot(world, allianceId));

    public int? Affiliation(int corporationId) =>
        LiveWorld.Find(database.Path)?.OwnerDirectory.Snapshot.AllianceMemberships.GetValueOrDefault(corporationId) is > 0 and var id
            ? id
            : null;

    public int? PilotAffiliation(long pilotId)
    {
        var snapshot = LiveWorld.Find(database.Path)?.OwnerDirectory.Snapshot;
        return
            snapshot?.Pilots.GetValueOrDefault(pilotId) is { } pilot
            && snapshot.AllianceMemberships.TryGetValue(pilot.CorporationId, out int alliance)
            ? alliance
            : null;
    }

    public IReadOnlyList<Alliance> List() =>
        Resident.Economy.Inspect(world =>
            (IReadOnlyList<Alliance>)world.Set<SavedAlliance>().OrderBy(row => row.Name).Select(row => Snapshot(world, row.Id)!).ToArray()
        );

    public IReadOnlyList<AllianceMember> Members(int allianceId) =>
        Resident.Economy.Inspect<IReadOnlyList<AllianceMember>>(world => ReadMembers(world, allianceId).ToArray());

    public IReadOnlyList<AllianceMember> MembersForPilot(long pilotId) =>
        Resident.Economy.Inspect<IReadOnlyList<AllianceMember>>(world =>
        {
            var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
            var viewer =
                world.Set<SavedAllianceMember>().Find(pilot.CorporationId)
                ?? throw new InvalidDataException("Your corporation is not in an alliance.");
            return ReadMembers(world, viewer.AllianceId).Select(member => member.ForViewer(viewer.CorporationId)).ToArray();
        });

    private static IEnumerable<AllianceMember> ReadMembers(WorldAction world, int allianceId) =>
        world
            .Filter<SavedAllianceMember>(row => row.AllianceId == allianceId)
            .OrderBy(row => row.CorporationId)
            .AsEnumerable()
            .Select(Member);

    public int DaysInAlliance(int allianceId, int corporationId) =>
        Resident.Economy.Inspect(world =>
            world.Set<SavedAllianceMember>().Find(corporationId) is { } member && member.AllianceId == allianceId
                ? AllianceMembershipRules.MembershipDays(member.JoinedAt, Now)
                : 0
        );

    public IReadOnlyList<int> MembersOlderThan(int allianceId, int minimumDays) =>
        Resident.Economy.Inspect(world =>
        {
            long now = Now;
            return world
                .Filter<SavedAllianceMember>(member => member.AllianceId == allianceId)
                .AsEnumerable()
                .Where(member => AllianceMembershipRules.MembershipDays(member.JoinedAt, now) >= minimumDays)
                .Select(member => member.CorporationId)
                .Order()
                .ToArray();
        });

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
