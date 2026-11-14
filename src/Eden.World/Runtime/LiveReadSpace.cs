using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Propulsion;
using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<int, IReadOnlyList<MissileContact>> _missileContacts = new(
        "resident.missileContacts",
        "WorldMissile"
    );

    internal sealed record NpcHostileAttempt(long SourceId, long StartedAt);

    internal bool HasNpcEwar(long ship) => ResidentNpcTarget(ship).HasRows;

    internal IReadOnlyList<NpcHostileAttempt> NpcEwarAttempts(long ship) => ResidentNpcTarget(ship).Attempts;

    internal IReadOnlyList<NpcEwarContact> NpcEwarIncoming(long ship) => ResidentNpcTarget(ship).Contacts;

    internal IReadOnlyList<NpcEwarContact> NpcEwarSource(long source) => ResidentNpcSource(source);

    private readonly ResidentSnapshot<int, bool> _activeCores = new("resident.activeCores", "WorldActiveModule", "WorldItemMembership");

    internal bool HasPilotMissiles(int system) =>
        ReadResidentGuard(
            () => owner.Economy.HasPilotMissiles(system),
            world =>
                world
                    .Set<WorldMissile>()
                    .Any(row =>
                        row.SourceKind == MissileSourceKind.Pilot
                        && row.SystemId == system
                        && (row.Status == MissileFlightStatus.Flying || row.Status == MissileFlightStatus.Attached)
                    )
        );

    internal bool HasMissileReports(long pilot) =>
        ReadResidentGuard(
            () => owner.Economy.HasMissileReports(pilot),
            world =>
                world
                    .Set<WorldMissile>()
                    .Any(row => row.SourceKind == MissileSourceKind.Pilot && row.PilotId == pilot && row.Outcome != null && !row.Reported)
        );

    internal bool Impacted(long id) =>
        ReadResidentGuard(
            () => owner.Economy.MissileImpacted(id),
            world => world.Set<WorldMissile>().Find(id)?.Status is MissileFlightStatus.Impact or MissileFlightStatus.Attached
        );

    internal bool HasNpcMissileWork(long pilot, long now, int historySeconds) => ResidentNpcMissileWork(pilot, now, historySeconds);

    internal IReadOnlyList<MissileContact> MissileContacts(int system, Func<IReadOnlyList<MissileContact>> load) =>
        Read(
            _missileContacts,
            system,
            () =>
                Array.AsReadOnly(
                    load().Select(contact => contact with { LaunchModules = Array.AsReadOnly(contact.LaunchModules.ToArray()) }).ToArray()
                )
        );

    internal IReadOnlyList<TackleEffect> Incoming(long ship) => ResidentTackleTarget(ship);

    internal bool HasTackleWork()
    {
        return ReadResidentGuard(() => owner.Economy.HasTackleRows, world => world.Set<WorldTackle>().Any())
            || Read(_activeCores, 0, ActiveCores);
    }

    private bool ActiveCores()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return owner.Economy.Inspect(world =>
            world
                .Set<ActiveFittedModule>()
                .Select(row => row.ModuleId)
                .AsEnumerable()
                .Any(id =>
                    world.Set<OwnedItem>().Find(id) is { } item
                    && catalog.FittingType(item.TypeId)?.GroupId == TackleDefinitions.WarpCoreStabilizerGroup
                )
        );
    }

    internal bool HasPlayerEwar() => ReadResidentGuard(() => owner.Economy.HasPlayerEwarRows, world => world.Set<WorldPlayerEwar>().Any());

    private readonly ResidentSnapshot<
        long,
        (IReadOnlyList<Eden.Gameplay.Fleets.CommandBurstBoost> Boosts, long NextExpiry)
    > _commandBursts = new("resident.commandBursts", "WorldCommandBurst", "CommandBurstRecipient");

    internal IReadOnlyList<Eden.Gameplay.Fleets.CommandBurstBoost> CommandBursts(
        long ship,
        long now,
        Func<long, long, (IReadOnlyList<Eden.Gameplay.Fleets.CommandBurstBoost> Boosts, long NextExpiry)> load
    ) => Read(_commandBursts, ship, () => load(ship, now), now, result => result.NextExpiry).Boosts;

    private readonly record struct EwarModuleKey(long Pilot, long Ship, long Module);

    private readonly ResidentSnapshot<EwarModuleKey, long> _signatureSuppression = new(
        "resident.signatureSuppression",
        "WorldActiveModule",
        "WorldItemMembership"
    );

    internal bool SignatureSuppressionActive(long pilot, long ship, long module, long now, Func<long, long, long, long> load) =>
        Read(_signatureSuppression, new(pilot, ship, module), () => load(pilot, ship, module)) > now;

    private readonly ResidentSnapshot<EwarModuleKey, long> _assaultDamageControls = new(
        "resident.assaultDamageControls",
        "WorldActiveModule",
        "WorldItemMembership"
    );

    internal bool AssaultDamageControlActive(long pilot, long ship, long module, long now, Func<long, long, long, long> load) =>
        Read(_assaultDamageControls, new(pilot, ship, module), () => load(pilot, ship, module)) > now;

    private readonly ResidentSnapshot<long, IReadOnlyList<RemoteAssistanceContact>> _remoteAssistance = new(
        "resident.remoteAssistance",
        "WorldRemoteAssistance"
    );
    private readonly ResidentSnapshot<EwarModuleKey, bool> _remoteAssistanceActive = new(
        "resident.remoteAssistanceActive",
        "WorldRemoteAssistance",
        "WorldItem",
        "WorldContractEscrow"
    );

    internal IReadOnlyList<RemoteAssistanceContact> RemoteAssistanceIncoming(
        long ship,
        Func<long, IReadOnlyList<RemoteAssistanceContact>> load
    ) => Read(_remoteAssistance, ship, () => load(ship));

    internal bool RemoteAssistanceActive(long pilot, long ship, long module, Func<long, long, long, bool> load) =>
        Read(_remoteAssistanceActive, new(pilot, ship, module), () => load(pilot, ship, module));

    private readonly ResidentSnapshot<EwarModuleKey, bool> _ewarOnline = new("resident.ewarOnline", "WorldItem", "WorldContractEscrow");

    internal bool EwarModuleOnline(long pilot, long ship, long module, Func<long, long, long, bool> load) =>
        Read(_ewarOnline, new(pilot, ship, module), () => load(pilot, ship, module));

    private readonly ResidentSnapshot<EwarModuleKey, bool> _remoteRepairOnline = new(
        "resident.remoteRepairOnline",
        "WorldItem",
        "WorldContractEscrow"
    );

    internal bool RemoteRepairModuleOnline(long pilot, long ship, long module, Func<long, long, long, bool> load) =>
        Read(_remoteRepairOnline, new(pilot, ship, module), () => load(pilot, ship, module));

    private readonly ResidentSnapshot<EwarModuleKey, bool> _capacitorOnline = new(
        "resident.capacitorOnline",
        "WorldItem",
        "WorldContractEscrow"
    );

    internal bool CapacitorModuleOnline(long pilot, long ship, long module, Func<long, long, long, bool> load) =>
        Read(_capacitorOnline, new(pilot, ship, module), () => load(pilot, ship, module));

    internal IReadOnlyList<ElectronicWarfareContact> PlayerEwarIncoming(long targetId) => ResidentPlayerEwarTarget(targetId);

    private void ObserveSpace(IReadOnlyCollection<CombatRowChange> changes)
    {
        _assaultDamageControls.Observe(changes, (key, change) => key.Module == change.Id);
        _capacitorOnline.Observe(changes, (key, change) => change.Table == "WorldContractEscrow" || key.Module == change.Id);
        _signatureSuppression.Observe(changes, (key, change) => change.Table == "WorldContractEscrow" || key.Module == change.Id);
        _missileContacts.Observe(changes);
        _activeCores.Observe(changes);
        _ewarOnline.Observe(changes, (key, change) => change.Table == "WorldContractEscrow" || key.Module == change.Id);
        _remoteRepairOnline.Observe(changes, (key, change) => change.Table == "WorldContractEscrow" || key.Module == change.Id);
        _remoteAssistance.Observe(changes);
        _remoteAssistanceActive.Observe(changes, (key, change) => change.Table == "WorldContractEscrow" || key.Module == change.Id);
    }

    private void ClearSpace()
    {
        _residentNpcTargets.Clear();
        _residentNpcSources.Clear();
        _residentTackleTargets.Clear();
        _residentPlayerEwarTargets.Clear();
        _assaultDamageControls.Clear();
        _capacitorOnline.Clear();
        _signatureSuppression.Clear();
        _missileContacts.Clear();
        _activeCores.Clear();
        _ewarOnline.Clear();
        _remoteRepairOnline.Clear();
        _remoteAssistance.Clear();
        _remoteAssistanceActive.Clear();
    }
}
