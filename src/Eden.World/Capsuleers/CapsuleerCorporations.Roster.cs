using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

public sealed record CapsuleerRosterOrigin(int SystemId, int Pilots);

public sealed record CapsuleerRosterReport(
    int CorporationId,
    int Members,
    int SimulatedMembers,
    CapsuleerRosterOrigin[] Origins,
    double LargestOriginShare,
    int PendingApplications,
    int PilotsRelocating,
    int PilotsWithWorkBase,
    int RepeatRelocations
);

internal static partial class CapsuleerCorporations
{
    internal static CapsuleerRosterReport? Roster(WorldDatabase database, int id) =>
        LiveWorld.Find(database.Path)?.Economy.Read(world => Roster(world, id));

    internal static CapsuleerRosterReport? Roster(WorldAction world, int id)
    {
        if (world.Set<SavedCorporation>().Find(id) is not { ClosedAt: null })
        {
            return null;
        }
        long[] members = world.Filter<Pilot>(pilot => pilot.CorporationId == id).Select(pilot => pilot.Id).ToArray();
        var agents = members.Select(pilot => world.Set<SavedCapsuleerAgent>().Find(pilot)).OfType<SavedCapsuleerAgent>().ToArray();
        var organizations = agents.Select(agent => Organization(world, agent.PilotId)).ToArray();
        var origins = agents
            .GroupBy(agent => agent.CommunitySystemId)
            .Select(group => new CapsuleerRosterOrigin(group.Key, group.Count()))
            .OrderByDescending(origin => origin.Pilots)
            .ThenBy(origin => origin.SystemId)
            .ToArray();
        int pending = world
            .Filter<SavedCorporationApplication>(application => application.CorporationId == id)
            .AsEnumerable()
            .Count(application => !CorporationMembershipRules.Closed(application.Status));
        return new(
            id,
            members.Length,
            agents.Length,
            origins,
            origins.Length == 0 ? 0 : (double)origins[0].Pilots / agents.Length,
            pending,
            organizations.Count(organization => organization.Relocation is not null),
            organizations.Count(organization => organization.OperatingStationId.HasValue),
            organizations.Sum(organization => Math.Max(0, organization.RelocationsCompleted - 1))
        );
    }
}
