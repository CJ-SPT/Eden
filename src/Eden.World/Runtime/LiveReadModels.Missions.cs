using Eden.World.Agents;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<int, MissionSitesView> _missionSites = new("resident.missionSites", "WorldMission");

    internal MissionSitesView MissionSites(int systemId, long now, Func<MissionSitesView> load) =>
        Read(_missionSites, systemId, load, now, view => view.ExpiresAt);
}
