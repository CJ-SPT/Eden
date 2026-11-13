using Eden.World.Information;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<long, DirectionalObserver> _directionalObservers = new(
        "resident.directionalObserver",
        "WorldPilot",
        "WorldShip",
        "WorldPosition",
        "WorldFitRevision",
        "WorldAccount"
    );

    private readonly ResidentSnapshot<int, IReadOnlyList<DirectionalEntry>> _directionalContacts = new(
        "resident.directionalContacts",
        "WorldShip",
        "WorldPosition",
        "WorldDrone",
        "WorldItem",
        "WorldNpc",
        "WorldWreck",
        "WorldJetcan",
        "WorldLogoff",
        "WorldCapsuleerSuspension"
    );

    internal DirectionalObserver DirectionalObserver(long pilot, Func<DirectionalObserver> load) =>
        Read(_directionalObservers, pilot, load);

    internal IReadOnlyList<DirectionalEntry> DirectionalContacts(int system, Func<IReadOnlyList<DirectionalEntry>> load) =>
        Read(_directionalContacts, system, load);
}
