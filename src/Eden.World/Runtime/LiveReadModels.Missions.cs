using Eden.World.Agents;
using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<long, MissionTrackingView> _missionTracking = new(
        "resident.missionTracking",
        "WorldMission",
        "WorldItem",
        "WorldPilot",
        "WorldMissionMember",
        "WorldMissionStructure"
    );

    internal MissionTrackingView MissionTracking(long pilot, long now, Func<MissionTrackingView> load) =>
        Read(_missionTracking, pilot, load, now, view => view.ExpiresAt);

    // The tracker derives location objectives from the session. The saved view
    // needs inventory only when an accepted mission checks cargo.
    private void ObserveMissionTracking(IReadOnlyCollection<CombatRowChange> changes) =>
        _missionTracking.ObserveValues(
            changes,
            (pilot, view, change) =>
                change.Table switch
                {
                    "WorldPilot" => pilot == change.Id,
                    "WorldItem" => view.Entries.Any(entry => entry.Offer.Cargo is not null || entry.Offer.Retrieval is not null),
                    "WorldMissionMember" or "WorldMissionStructure" => view.Entries.Count != 0,
                    _ => true,
                }
        );

    private readonly ResidentSnapshot<int, MissionSitesView> _missionSites = new(
        "resident.missionSites",
        "WorldMission",
        "WorldMissionRoom",
        "WorldMissionStructure",
        "WorldAsteroid"
    );

    internal MissionSitesView MissionSites(int systemId, long now, Func<MissionSitesView> load) =>
        Read(_missionSites, systemId, load, now, view => view.ExpiresAt);
}
