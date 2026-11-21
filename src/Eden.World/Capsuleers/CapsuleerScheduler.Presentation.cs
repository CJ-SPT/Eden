using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.World.Runtime;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private sealed record SystemPresentation(
        LivePresentationVersion Version,
        long Catalog,
        long Generation,
        long CapturedAt,
        long ValidUntil,
        IReadOnlyList<MinerObservation> Miners,
        IReadOnlyList<HaulerObservation> Haulers
    );

    private readonly Dictionary<int, SystemPresentation> _presentations = [];
    private bool _presentationHadPending;

    private readonly record struct LocalPresence(int System, int Region, string Name, int Corporation, int Gender);

    private readonly Dictionary<long, LocalPresence> _offscreenPresence = [];

    private bool NeedsPresentation(DockedPilot pilot, int departed, IReadOnlyList<int> observed)
    {
        // An observed departure removes its ship from the grid. Docked updates also carry
        // the station guest's full current ship and identity. In empty space only Local and
        // online membership consume the handoff until a human enters and gets the full scene.
        if (pilot.Position is null || observed.Contains(departed) || observed.Contains(pilot.SystemId))
        {
            _offscreenPresence.Remove(pilot.PilotId);
            return true;
        }
        LocalPresence current = new(pilot.SystemId, pilot.RegionId, pilot.Name, pilot.CorpId, pilot.Profile.GenderId);
        if (_offscreenPresence.TryGetValue(pilot.PilotId, out var previous) && previous == current)
        {
            return false;
        }
        _offscreenPresence[pilot.PilotId] = current;
        return true;
    }

    private SystemPresentation ReadPresentation(SystemSimulationState state, long now, out bool changed)
    {
        var live = Live;
        using var context = live?.UseSystem(state.SystemId);
        // Enter before checking the version: timed effects expire at this boundary.
        // A cached view must never bypass those authoritative completions.
        using var ownership = synchronization.Enter("traffic observation", state.SystemId);
        var version = live?.PresentationVersion(state.SystemId) ?? default;
        long catalog = database.Catalog.Identity.Generation;
        _presentations.TryGetValue(state.SystemId, out var previous);
        if (
            live is not null
            && previous is not null
            && previous.Version == version
            && previous.Catalog == catalog
            && previous.Generation == state.Generation
            && now >= previous.CapturedAt
            && now < previous.ValidUntil
        )
        {
            changed = false;
            return previous;
        }
        List<MinerObservation> mining = [];
        List<HaulerObservation> hauling = [];
        long until = live is null ? now : long.MaxValue;
        foreach (long pilot in _systemAgents.GetValueOrDefault(state.SystemId) ?? [])
        {
            if (_suspended.Contains(pilot) || _locations[pilot] != state.SystemId)
            {
                continue;
            }
            if (MinerWorker(pilot) is { } miner)
            {
                PresentationReads++;
                mining.Add(miner.Read()!);
            }
            if (HaulerWorker(pilot) is { } hauler)
            {
                PresentationReads++;
                hauling.Add(hauler.Read()!);
            }
            until = Math.Min(until, live?.CombatForSystem(state.SystemId)?.HealthValidUntil(pilot, now) ?? now);
        }
        IReadOnlyList<MinerObservation> minersView =
            previous is not null && previous.Miners.SequenceEqual(mining) ? previous.Miners : mining.ToArray();
        IReadOnlyList<HaulerObservation> haulersView =
            previous is not null && previous.Haulers.SequenceEqual(hauling) ? previous.Haulers : hauling.ToArray();
        changed = previous is null || !ReferenceEquals(previous.Miners, minersView) || !ReferenceEquals(previous.Haulers, haulersView);
        var result = new SystemPresentation(
            // Reads may settle a repair or cross an effect deadline. Retaining the
            // starting version forces another capture if publication changed meanwhile.
            version,
            catalog,
            state.Generation,
            now,
            until,
            minersView,
            haulersView
        );
        _presentations[state.SystemId] = result;
        return result;
    }

    public CapsuleerBatch Capture()
    {
        using (synchronization.Enter("CapsuleerScheduler"))
        {
            Load();
            var activity = presence.Read();
            Reconcile(activity);
            SettleReadySystems(activity);
            var states = _systems.Observed();
            var active = states.Where(state => state.Mode == SystemSimulationMode.Active).Select(state => state.SystemId).ToHashSet();
            bool contentChanged =
                _lastPresentation is null
                || _presentationHadPending
                || _pendingMiners.Count + _pendingHaulers.Count > 0
                || !SameSystems(_lastPresentation.Systems, states);
            foreach (int system in _presentations.Keys.Where(system => !active.Contains(system)).ToArray())
            {
                _presentations.Remove(system);
                _readiness.Remove(system);
                contentChanged = true;
            }
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            foreach (var state in states)
            {
                if (state.Mode == SystemSimulationMode.Active)
                {
                    ReadPresentation(state, now, out bool changed);
                    contentChanged |= changed;
                }
            }
            if (!contentChanged && _lastPresentation is { } unchanged)
            {
                _lastPresentation = unchanged.Systems.SequenceEqual(states) ? unchanged : unchanged with { Systems = states };
                return _lastPresentation;
            }
            _presentationHadPending = _pendingMiners.Count + _pendingHaulers.Count > 0;
            foreach (var state in states)
            {
                if (state.Mode != SystemSimulationMode.Active)
                {
                    continue;
                }
                var presentation = _presentations[state.SystemId];
                foreach (var miner in presentation.Miners)
                {
                    _pendingMiners[miner.Pilot.PilotId] = miner;
                }
                foreach (var hauler in presentation.Haulers)
                {
                    _pendingHaulers[hauler.Pilot.PilotId] = hauler;
                }
            }
            CapsuleerBatch batch = new(
                new(0, _registrations.Count, null, false),
                _pendingMiners.Values.ToArray(),
                _pendingHaulers.Values.ToArray()
            )
            {
                Revision = _presentationRevision,
                Systems = states,
            };
            _pendingMiners.Clear();
            _pendingHaulers.Clear();
            if (
                _lastPresentation is { } previous
                && previous.Miners.SequenceEqual(batch.Miners)
                && previous.Haulers.SequenceEqual(batch.Haulers)
                && previous
                    .Systems.Select(state => (state.SystemId, state.Mode, state.Generation))
                    .SequenceEqual(states.Select(state => (state.SystemId, state.Mode, state.Generation)))
            )
            {
                batch = previous.Systems.SequenceEqual(states) ? previous : batch with { Revision = previous.Revision };
            }
            else
            {
                batch = batch with { Revision = checked(++_presentationRevision) };
            }
            _lastPresentation = batch;
            return batch;
        }
    }

    private static bool SameSystems(IReadOnlyList<SystemSimulationState> previous, IReadOnlyList<SystemSimulationState> current)
    {
        if (previous.Count != current.Count)
        {
            return false;
        }
        for (int index = 0; index < current.Count; index++)
        {
            var before = previous[index];
            var after = current[index];
            if (before.SystemId != after.SystemId || before.Mode != after.Mode || before.Generation != after.Generation)
            {
                return false;
            }
        }
        return true;
    }
}
