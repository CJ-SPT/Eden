using System.Collections.Frozen;
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
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private sealed record MissileWork(
        MissileSourceKind Source,
        int System,
        long Pilot,
        long Id,
        MissileFlightStatus Status,
        bool PendingReport,
        long? EndedAt
    );

    private sealed record MissileIndex(
        FrozenSet<int> PilotSystems,
        FrozenSet<long> NpcPilots,
        FrozenSet<long> Reports,
        FrozenSet<long> Impacts,
        long? OldestNpcEnd
    );

    private readonly ResidentSnapshot<int, MissileIndex> _missileWork = new("resident.missileWork", "WorldMissile");
    private readonly ResidentSnapshot<int, IReadOnlyList<MissileContact>> _missileContacts = new(
        "resident.missileContacts",
        "WorldMissile"
    );
    private readonly ResidentSnapshot<int, IReadOnlyList<TackleEffect>> _tackle = new("resident.tackle", "WorldTackle");

    private sealed record NpcEwarWork(long TargetShipId, NpcEwarContact? Contact);

    private readonly ResidentSnapshot<int, IReadOnlyList<NpcEwarWork>> _npcEwar = new("resident.npcEwar", "WorldNpcEwar");

    private IReadOnlyList<NpcEwarWork> NpcEwar() =>
        Read(
            _npcEwar,
            0,
            () =>
            {
                using WorldDbContext world = new(database.Path);
                return world
                    .Set<WorldNpcEwar>()
                    .AsNoTracking()
                    .AsEnumerable()
                    .Select(e => new NpcEwarWork(e.TargetShipId, e.Contact()))
                    .ToArray();
            }
        );

    internal bool HasNpcEwar(long ship) => NpcEwar().Any(e => e.TargetShipId == ship);

    internal IReadOnlyList<NpcEwarContact> NpcEwarIncoming(long ship) =>
        NpcEwar().Where(e => e.TargetShipId == ship).Select(e => e.Contact).OfType<NpcEwarContact>().ToArray();

    internal IReadOnlyList<NpcEwarContact> NpcEwarSource(long source) =>
        NpcEwar().Select(e => e.Contact).OfType<NpcEwarContact>().Where(e => e.SourceId == source).ToArray();

    private readonly ResidentSnapshot<int, bool> _activeCores = new("resident.activeCores", "WorldActiveModule", "WorldItemMembership");

    private MissileIndex Missiles() =>
        Read(
            _missileWork,
            0,
            () =>
            {
                using WorldDbContext world = new(database.Path);
                var rows = world
                    .Set<WorldMissile>()
                    .Select(m => new MissileWork(
                        m.SourceKind,
                        m.SystemId,
                        m.PilotId,
                        m.Id,
                        m.Status,
                        m.Outcome != null && !m.Reported,
                        m.EndedAt
                    ))
                    .ToArray();
                return new(
                    rows.Where(m => m.Source == MissileSourceKind.Pilot && m.Status == MissileFlightStatus.Flying)
                        .Select(m => m.System)
                        .ToFrozenSet(),
                    rows.Where(m => m.Source == MissileSourceKind.Npc && m.Status == MissileFlightStatus.Flying)
                        .Select(m => m.Pilot)
                        .ToFrozenSet(),
                    rows.Where(m => m.Source == MissileSourceKind.Pilot && m.PendingReport).Select(m => m.Pilot).ToFrozenSet(),
                    rows.Where(m => m.Status == MissileFlightStatus.Impact).Select(m => m.Id).ToFrozenSet(),
                    rows.Where(m => m.Source == MissileSourceKind.Npc).Min(m => m.EndedAt)
                );
            }
        );

    internal bool HasPilotMissiles(int system) => Missiles().PilotSystems.Contains(system);

    internal bool HasMissileReports(long pilot) => Missiles().Reports.Contains(pilot);

    internal bool Impacted(long id) => Missiles().Impacts.Contains(id);

    internal bool HasNpcMissileWork(long pilot, long now, int historySeconds)
    {
        // Preserve the old unknown-pilot rejection even when the flying set is empty.
        _ = Learning(pilot);
        var index = Missiles();
        return index.NpcPilots.Contains(pilot) || index.OldestNpcEnd < now - historySeconds * TimeSpan.TicksPerSecond;
    }

    internal IReadOnlyList<MissileContact> MissileContacts(int system, Func<IReadOnlyList<MissileContact>> load) =>
        Read(
            _missileContacts,
            system,
            () =>
                Array.AsReadOnly(
                    load().Select(contact => contact with { LaunchModules = Array.AsReadOnly(contact.LaunchModules.ToArray()) }).ToArray()
                )
        );

    private IReadOnlyList<TackleEffect> Tackles() =>
        Read(
            _tackle,
            0,
            () =>
            {
                using WorldDbContext world = new(database.Path);
                return Array.AsReadOnly(
                    world.Set<WorldTackle>().OrderBy(t => t.ModuleId).AsEnumerable().Select(t => t.Contact()).ToArray()
                );
            }
        );

    internal IReadOnlyList<TackleEffect> Incoming(long ship) =>
        Array.AsReadOnly(Tackles().Where(t => t.Target?.Kind == TackleTargetKind.Ship && t.Target.Id == ship).ToArray());

    internal bool HasTackleWork() =>
        Tackles().Count > 0
        || Read(
            _activeCores,
            0,
            () =>
            {
                using WorldDbContext world = new(database.Path);
                using CatalogDbContext catalog = new(database.CatalogPath);
                return (
                    from active in world.Set<ActiveFittedModule>()
                    join item in world.Set<OwnedItem>() on active.ModuleId equals item.Id
                    select item.TypeId
                )
                    .AsEnumerable()
                    .Any(type => catalog.FittingType(type)?.GroupId == TackleDefinitions.WarpCoreStabilizerGroup);
            }
        );

    private readonly ResidentSnapshot<int, IReadOnlyList<ElectronicWarfareContact>> _playerEwar = new(
        "resident.playerEwar",
        "WorldPlayerEwar"
    );

    private IReadOnlyList<ElectronicWarfareContact> PlayerEwar() =>
        Read(
            _playerEwar,
            0,
            () =>
            {
                using WorldDbContext world = new(database.Path);
                return Array.AsReadOnly(
                    world
                        .Set<WorldPlayerEwar>()
                        .OrderBy(e => e.ModuleId)
                        .ThenBy(e => e.TargetId)
                        .AsNoTracking()
                        .AsEnumerable()
                        .Select(e => e.Contact())
                        .ToArray()
                );
            }
        );

    internal bool HasPlayerEwar() => PlayerEwar().Count > 0;

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

    internal IReadOnlyList<ElectronicWarfareContact> PlayerEwarIncoming(long targetId) =>
        Array.AsReadOnly(PlayerEwar().Where(e => e.Target.Id == targetId).ToArray());

    private void ObserveSpace(IReadOnlyCollection<CombatRowChange> changes)
    {
        _signatureSuppression.Observe(changes);
        _missileWork.Observe(changes);
        _missileContacts.Observe(changes);
        _tackle.Observe(changes);
        _activeCores.Observe(changes);
        _npcEwar.Observe(changes);
        _playerEwar.Observe(changes);
        _ewarOnline.Observe(changes);
        _remoteRepairOnline.Observe(changes);
        _remoteAssistance.Observe(changes);
        _remoteAssistanceActive.Observe(changes);
    }

    private void ClearSpace()
    {
        _signatureSuppression.Clear();
        _missileWork.Clear();
        _missileContacts.Clear();
        _tackle.Clear();
        _activeCores.Clear();
        _npcEwar.Clear();
        _playerEwar.Clear();
        _ewarOnline.Clear();
        _remoteRepairOnline.Clear();
        _remoteAssistance.Clear();
        _remoteAssistanceActive.Clear();
    }
}
