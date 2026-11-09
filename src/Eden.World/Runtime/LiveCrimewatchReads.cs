using Eden.Gameplay.Combat;
using Eden.World.Combat;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<int, ConcordSweep> _concordOffline = new(
        "resident.concordOffline",
        "WorldConcordResponse",
        "WorldCrimewatch",
        "WorldLogoff",
        "WorldPilot",
        "WorldShip"
    );

    internal ConcordSweep ConcordOffline(long now, Func<ConcordSweep> load) =>
        Read(_concordOffline, 0, load, now, value => value.NextExpiry);

    private readonly ResidentSnapshot<long, long> _concord = new(
        "resident.concord",
        "WorldConcordResponse",
        "WorldCrimewatch",
        "WorldPilot",
        "WorldShip",
        "WorldPositionMembership"
    );

    internal long ConcordDeadline(long pilot, Func<long> load) => Read(_concord, pilot, load);

    private readonly record struct CrimewatchKey(long PilotId, int SystemId);

    private readonly ResidentSnapshot<CrimewatchKey, CrimewatchSnapshot> _crimewatch = new(
        "resident.crimewatch",
        "WorldCrimewatch",
        "WorldEngagement",
        "WorldPilot",
        "WorldAccount"
    );
    private readonly ResidentSnapshot<int, IReadOnlyList<LoggedOffPilot>> _loggedOff = new(
        "resident.logoff",
        "WorldLogoff",
        "WorldShipLoss",
        "WorldPilot",
        "WorldShip",
        "WorldPosition",
        "WorldTackle"
    );

    internal CrimewatchSnapshot Crimewatch(long pilot, int system, Func<CrimewatchSnapshot> load) =>
        Read(_crimewatch, new(pilot, system), load);

    internal IReadOnlyList<LoggedOffPilot> LoggedOff(int system, Func<IReadOnlyList<LoggedOffPilot>> load) =>
        Read(_loggedOff, system, load);
}
