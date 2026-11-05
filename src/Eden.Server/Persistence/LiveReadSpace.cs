using System.Collections.Frozen;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Propulsion;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

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
    private readonly ResidentSnapshot<int, bool> _activeModules = new("resident.activeModules", "WorldActiveModule");

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
            _activeModules,
            0,
            () =>
            {
                using WorldDbContext world = new(database.Path);
                return world.Set<ActiveFittedModule>().Any();
            }
        );

    private void ObserveSpace(IReadOnlyCollection<CombatRowChange> changes)
    {
        _missileWork.Observe(changes);
        _missileContacts.Observe(changes);
        _tackle.Observe(changes);
        _activeModules.Observe(changes);
    }

    private void ClearSpace()
    {
        _missileWork.Clear();
        _missileContacts.Clear();
        _tackle.Clear();
        _activeModules.Clear();
    }
}
