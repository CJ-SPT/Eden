using Eden.Gameplay.Agents;
using Eden.World.Agents;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<long, IReadOnlyList<MissionTrackingEntry>> _missionTracking = new(
        "resident.missionTracking",
        "WorldMission",
        "WorldItem",
        "WorldPilot",
        "WorldShip",
        "WorldPositionMembership",
        "WorldMissionMember",
        "WorldMissionRoom",
        "WorldMissionStructure"
    );

    internal IReadOnlyList<MissionTrackingEntry> MissionTracking(long pilot, long now, Func<IReadOnlyList<MissionTrackingEntry>> load) =>
        Read(_missionTracking, pilot, load, now, entries => entries.Select(e => e.Offer.ExpiresAt).DefaultIfEmpty(long.MaxValue).Min());

    private readonly ResidentSnapshot<int, MissionSitesView> _missionSites = new(
        "resident.missionSites",
        "WorldMission",
        "WorldMissionRoom",
        "WorldMissionStructure"
    );

    internal MissionSitesView MissionSites(int systemId, long now, Func<MissionSitesView> load) =>
        Read(_missionSites, systemId, load, now, view => view.ExpiresAt);
}
