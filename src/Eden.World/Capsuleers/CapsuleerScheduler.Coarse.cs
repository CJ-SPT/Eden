using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private readonly CapsuleerBackgroundBudget _backgroundBudget = backgroundBudget ?? new(clock);
    private readonly Dictionary<int, CapsuleerCoarseWork> _offlineCoarseWork = [];
    private IReadOnlyDictionary<int, CapsuleerCoarseWork> _coarseWork => Live?.CapsuleerRoutines.Work ?? _offlineCoarseWork;
    private readonly Dictionary<int, SortedSet<long>> _coarseMembers = [];
    private readonly SortedSet<(long Due, long Order, int System)> _coarseQueue = [];
    private readonly SortedSet<(long Settled, int System)> _coarseFreshness = [];
    private readonly Dictionary<long, CapsuleerResumeWork> _resumePilots = [];
    private readonly Dictionary<long, long> _offlineCoarseSettled = [];
    private IReadOnlyDictionary<long, long> _coarseSettled => Live?.CapsuleerRoutines.Settled ?? _offlineCoarseSettled;
    private readonly HashSet<long> _pendingArrivals = [];
    private readonly HashSet<long> _admittedTravelers = [];
    private readonly Dictionary<long, CoarseRoleInputs> _coarseInputs = [];
    private long _coarseOrder;
    private int _coarseAgentCount;
    private long _coarseRetryTicks;
    private long _skippedRemoteTicks;
    private const int RecentReturnLimit = 32;
    private readonly Dictionary<int, CapsuleerReturnProgress> _returnProgress = [];
    private IReadOnlyList<CapsuleerReturnProgress> _recentReturns = [];

    private readonly record struct CoarseRoleInputs(
        long PilotRevision,
        long ShipRevision,
        long AgentRevision,
        long ResetRevision,
        long CatalogGeneration
    );

    private CoarseRoleInputs ReadCoarseInputs(long pilot) =>
        new(
            Live!.CapsuleerInputRevisions.GetValueOrDefault(pilot),
            Live.CapsuleerShipRevisions.GetValueOrDefault(_pilotShips[pilot]),
            Live.CapsuleerAgentRevisions.GetValueOrDefault(pilot),
            Live.CapsuleerInputReset,
            database.Catalog.Identity.Generation
        );

    private void RefreshCoarseRole(long pilot)
    {
        if (!_coarseInputs.TryGetValue(pilot, out var previous) || previous == ReadCoarseInputs(pilot))
        {
            return;
        }
        if (_miners.ContainsKey(pilot))
        {
            _miners[pilot] = miners.Create(pilot);
        }
        else
        {
            _haulers[pilot] = haulers.Create(pilot);
        }
        Live!.AssignCapsuleer(pilot, null, _locations[pilot], null, null);
        _coarseInputs[pilot] = ReadCoarseInputs(pilot);
    }

    private void RecordDemotions(IReadOnlyList<long> pilots, long elapsed)
    {
        if (Live is null || pilots.Count == 0)
        {
            return;
        }
        foreach (long pilot in pilots)
        {
            Live.CapsuleerRoutines.Settle(pilot, Math.Max(_coarseSettled.GetValueOrDefault(pilot), elapsed));
        }
    }

    private void RecordReturn(CapsuleerResumeWork resumed)
    {
        long skipped = Math.Max(0, resumed.EntryTicks - Math.Max(resumed.SettledTicks, resumed.PreviousDueTicks));
        _skippedRemoteTicks += skipped;
        if (skipped == 0)
        {
            return;
        }
        var previous = _returnProgress.GetValueOrDefault(resumed.SystemId);
        if (previous?.EntryTicks != resumed.EntryTicks)
        {
            previous = new(resumed.SystemId, resumed.EntryTicks, clock.GetUtcNow(), 0, 0);
        }
        _returnProgress[resumed.SystemId] = previous with
        {
            Bots = previous.Bots + 1,
            MaximumSkippedTicks = Math.Max(previous.MaximumSkippedTicks, skipped),
        };
        var ordered = _returnProgress.Values.OrderByDescending(r => r.EntryTicks).ThenBy(r => r.SystemId).ToArray();
        foreach (var expired in ordered.Skip(RecentReturnLimit))
        {
            _returnProgress.Remove(expired.SystemId);
        }
        _recentReturns = Array.AsReadOnly(ordered.Take(RecentReturnLimit).ToArray());
    }

    private void ReconcileCoarse(HumanActivitySnapshot activity)
    {
        if (Live is null)
        {
            return;
        }
        foreach (int system in _coarseMembers.Keys)
        {
            _systems.SetCoarse(system, false);
        }
        _coarseMembers.Clear();
        foreach (var location in _locations)
        {
            long pilot = location.Key;
            if (
                _running.Contains(pilot)
                || _suspended.Contains(pilot)
                || (!_remoteSystems.Contains(_communities.GetValueOrDefault(pilot)) && !_admittedTravelers.Contains(pilot))
            )
            {
                continue;
            }
            if (!_coarseMembers.TryGetValue(location.Value, out var members))
            {
                members = [];
                _coarseMembers.Add(location.Value, members);
            }
            members.Add(pilot);
        }
        _coarseQueue.Clear();
        _coarseFreshness.Clear();
        _coarseAgentCount = _coarseMembers.Values.Sum(m => m.Count);
        foreach (int system in _coarseMembers.Keys)
        {
            if (!_coarseWork.TryGetValue(system, out var work))
            {
                long due = CapsuleerCoarseRules.FirstDue(system, activity.ElapsedTicks);
                work = new(system, 0, 0, activity.ElapsedTicks, due);
                Live.CapsuleerRoutines.Publish(work);
            }
            _systems.SetCoarse(system, true);
            _coarseQueue.Add((work.NextTicks, ++_coarseOrder, system));
            _coarseFreshness.Add((work.SettledTicks, system));
        }
        foreach (int system in activity.ActiveSystems)
        {
            _systems.SetCoarse(system, false);
        }
    }

    private void AdmitTraveler(long pilot)
    {
        if (Live is null || !_admittedTravelers.Add(pilot))
        {
            return;
        }
        Live.CapsuleerRoutines.Admit(pilot);
    }

    private long? NextCoarseDue(HumanActivitySnapshot activity) =>
        !activity.Active || _coarseQueue.Count == 0 ? null : Math.Max(_coarseQueue.Min.Due, _coarseRetryTicks);

    private CapsuleerBatch IncludeCoarseDue(CapsuleerBatch batch)
    {
        using var owner = synchronization.Enter("coarse wake publication");
        var activity = presence.Read();
        if (batch.Turn.OldestDueTicks is null && NextCoarseDue(activity) is { } due && due <= activity.ElapsedTicks)
        {
            return batch with { Turn = batch.Turn with { OldestDueTicks = due } };
        }
        return batch;
    }

    private bool RunCoarse(int stamp, CapsuleerHauler.PreparedJourney? journey = null, bool measured = false)
    {
        try
        {
            return RunCoarseCore(stamp, journey, measured);
        }
        catch (CapsuleerCoarseConflictException)
        {
            using var owner = synchronization.Enter("coarse cursor refresh");
            _membershipDirty = true;
            return false;
        }
        catch (Exception error)
        {
            using var owner = synchronization.Enter("coarse failure publication");
            _failures++;
            _lastFailure = error.Message;
            var activity = presence.Read();
            if (_coarseQueue.Count > 0)
            {
                var failed = _coarseQueue.Min;
                _coarseQueue.Remove(failed);
                _coarseQueue.Add((checked(activity.ElapsedTicks + TimeSpan.TicksPerSecond), ++_coarseOrder, failed.System));
            }
            PublishDiagnostics(activity, clock.GetTimestamp());
            throw;
        }
    }

    private bool RunCoarseCore(int stamp, CapsuleerHauler.PreparedJourney? journey, bool measured)
    {
        using var owner = synchronization.Enter("coarse traffic selection");
        Load();
        var activity = presence.Read();
        Reconcile(activity);
        if (
            !activity.Active
            || _queue.NextDueTicks <= activity.ElapsedTicks
            || NextCoarseDue(activity) is not { } due
            || due > activity.ElapsedTicks
        )
        {
            return false;
        }
        var selected = _coarseQueue.Min;
        var work = _coarseWork[selected.System];
        var members = _coarseMembers[selected.System];
        // Only a bounded page is materialized. One bot settlement yields ownership
        // before another bot; the durable cursor advances in that same transaction.
        long pilot = members.GetViewBetween(checked(work.Cursor + 1), long.MaxValue).Take(CapsuleerCoarseRules.PageSize).FirstOrDefault();
        string operationName =
            pilot == 0
                ? $"coarse simulation pass system={selected.System}"
                : $"coarse simulation {Current[pilot].Action} pilot={pilot} system={selected.System}";
        using var budget = _backgroundBudget.TryEnter(operationName, pilot == 0 ? null : Current[pilot].Action);
        if (budget is null)
        {
            _coarseRetryTicks = checked(activity.ElapsedTicks + CapsuleerCoarseRules.Window.Ticks);
            return false;
        }
        if (pilot == 0)
        {
            var next = work with
            {
                Generation = checked(work.Generation + 1),
                Cursor = 0,
                ThroughTicks = activity.ElapsedTicks,
                SettledTicks = activity.ElapsedTicks,
                NextTicks = CapsuleerCoarseRules.NextDue(activity.ElapsedTicks),
            };
            Live!.CapsuleerRoutines.Validate(work);
            _coarseQueue.Remove(selected);
            _coarseQueue.Add((next.NextTicks, ++_coarseOrder, next.SystemId));
            Live.CapsuleerRoutines.Publish(next);
            _coarseFreshness.Remove((work.SettledTicks, work.SystemId));
            _coarseFreshness.Add((next.SettledTicks, next.SystemId));
            return true;
        }
        var job = Current[pilot];
        RefreshCoarseRole(pilot);
        var input = work with
        {
            ThroughTicks = activity.ElapsedTicks,
            Systems = (_routes.GetValueOrDefault(pilot) ?? []).Append(_locations[pilot]).Distinct().Order().ToArray(),
        };
        using var context = Live!.UseSystem(_locations[pilot]);
        using var roleOwnership = Live.EnterTransactionSystems(new(systems: [_locations[pilot]]));
        using var measurement = measured
            ? null
            : Live.TrafficActions.Measure(job, [_locations[pilot]], () => (Live.CheckpointCopies, Live.CheckpointCopying));
        CapsuleerJob settled;
        _miners.TryGetValue(pilot, out var miner);
        if (job.DueTicks > activity.ElapsedTicks)
        {
            settled = job.Next(job.DueTicks, job.Action);
            using CapsuleerOperation operation = new(database, job, marketSignal, input);
            operation.Commit(settled, null, null);
        }
        else if (miner is not null)
        {
            settled = miner.Execute(job, stamp, activity.ElapsedTicks, activity.Resumes, background: true, coarse: input);
            IndexLocation(pilot, miner.SystemId);
        }
        else
        {
            var hauler = _haulers[pilot];
            settled = hauler.Execute(job, stamp, background: true, preparedJourney: journey, coarse: input);
            IndexJourney(pilot, hauler);
            IndexLocation(pilot, hauler.SystemId);
        }
        var loadedPilot = miner?.PreparationPilot ?? _haulers.GetValueOrDefault(pilot)?.PreparationPilot;
        if (loadedPilot is not null)
        {
            RegisterShip(pilot, loadedPilot.ShipId);
        }
        Current[pilot] = settled;
        // Commit publishes progress and its cursor together under shared ownership.
        _coarseQueue.Remove(selected);
        _coarseQueue.Add((activity.ElapsedTicks, ++_coarseOrder, selected.System));
        _completed++;
        // The immutable checkpoints retain all paid work, not a hydrated scene.
        int location = _locations[pilot];
        _detailedSystems.Add(selected.System);
        bool detailed = Detailed(pilot, activity);
        _miners.GetValueOrDefault(pilot)?.SetDetailed(detailed);
        _haulers.GetValueOrDefault(pilot)?.SetDetailed(detailed);
        bool released = !detailed && Live.ReleaseSystemDetails(location);
        if (!detailed && !released)
        {
            _combatPinnedSystems.Add(location);
            _systems.Pin(location, true);
        }
        if (released)
        {
            // SetDetailed(false) releases geometry/contacts while preserving the compact
            // pilot, navigation and role inputs. Rebuilding the role here forced a cold
            // pilot/fitting/route load for every remote action. Committed input revisions
            // invalidate this snapshot before either its next settlement or activation.
            _coarseInputs[pilot] = ReadCoarseInputs(pilot);
            Live.AssignCapsuleer(pilot, null, _locations[pilot], null, null);
        }
        var hauling = Live.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work;
        if (
            settled.Action == CapsuleerAction.GateTransfer
            && hauling?.TargetId is > 0 and <= int.MaxValue
            && database.Catalog.Gates.TryGetValue((int)hauling.TargetId, out var gate)
            && activity.ActiveSystems.Contains(gate.DestinationSystemId)
        )
        {
            _pendingArrivals.Add(pilot);
        }
        _membershipDirty |= detailed || !released || location != selected.System || _pendingArrivals.Contains(pilot);
        PublishDiagnostics(activity, clock.GetTimestamp());
        return true;
    }
}
