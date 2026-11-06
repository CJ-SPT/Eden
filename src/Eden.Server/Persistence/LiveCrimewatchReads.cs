using Eden.Gameplay.Combat;

namespace Eden.Server.Persistence;

internal sealed partial class LiveReadModels
{
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
