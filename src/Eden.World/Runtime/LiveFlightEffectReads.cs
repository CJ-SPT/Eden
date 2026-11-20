using Eden.Gameplay.Flight;
using Eden.Gameplay.Sovereignty;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<(int System, int Constellation), bool> _sovereigntyWork = new("resident.sovereigntyWork");
    private readonly ResidentSnapshot<long, IReadOnlyList<EntosisCycle>> _entosisCycles = new("resident.entosisCycles");
    private readonly ResidentSnapshot<(long Ship, long Catalog, long CycleRevision), EntosisLimit> _entosisLimits = new(
        "resident.entosisLimit",
        "WorldItemMembership",
        "WorldItem",
        "WorldContractEscrow"
    );
    private readonly ResidentSnapshot<int, InterdictionContacts> _interdictionContacts = new(
        "resident.interdictionContacts",
        "SavedInterdictionField"
    );

    internal readonly record struct EntosisLimit(double? Speed, long ExpiresAt);

    internal sealed record InterdictionContacts(IReadOnlyList<StaticSpaceObject> Objects, long ExpiresAt);

    internal bool SovereigntyWork(int system, int constellation, Func<WorldAction, bool> load) =>
        ReadResidentVersion(_sovereigntyWork, (system, constellation), () => owner.SovereigntyWorkRevision, load);

    internal IReadOnlyList<EntosisCycle> EntosisCycles(long pilot, Func<WorldAction, IReadOnlyList<EntosisCycle>> load) =>
        ReadResidentVersion(_entosisCycles, pilot, () => owner.EntosisCycleRevision, load);

    internal EntosisLimit EntosisSpeedLimit(long ship, long catalog, long now, Func<EntosisLimit> load) =>
        Read(_entosisLimits, (ship, catalog, owner.EntosisCycleRevision), load, now, limit => limit.ExpiresAt);

    internal InterdictionContacts Interdiction(int system, long now, Func<InterdictionContacts> load) =>
        Read(_interdictionContacts, system, load, now, contacts => contacts.ExpiresAt);

    private void ObserveFlightEffects(IReadOnlyCollection<CombatRowChange> changes)
    {
        _entosisLimits.Observe(changes, (_, change) => change.Table != "WorldItem" || !change.Resident);
        _interdictionContacts.Observe(changes);
    }

    private void ClearFlightEffects()
    {
        _sovereigntyWork.Clear();
        _entosisCycles.Clear();
        _entosisLimits.Clear();
        _interdictionContacts.Clear();
    }
}
