using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private const int TradeWorkers = 2;
    private const int TradeLookAhead = 256;
    private const int RetainedTrades = 8;

    private sealed record UpcomingTrade(PreparationInput Input, Task<CapsuleerHauler.PreparedJourney?> Work);

    private readonly Dictionary<CapsuleerJob, UpcomingTrade> _upcomingTrades = [];
    private readonly object _tradeGate = new();
    private bool _disposed;
    internal long TradesStartedAhead { get; private set; }
    internal long TradesUsedAhead { get; private set; }

    private void StartUpcomingTrades(CapsuleerJob head, HumanActivitySnapshot activity, CancellationToken cancellationToken)
    {
        if (Live is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }
        lock (_tradeGate)
        {
            if (_disposed)
            {
                return;
            }
            foreach (var (job, trade) in _upcomingTrades.ToArray())
            {
                if (trade.Work.IsCompleted && (!_running.Contains(job.PilotId) || Current.GetValueOrDefault(job.PilotId) != job))
                {
                    _ = trade.Work.Exception;
                    _upcomingTrades.Remove(job);
                }
            }
            int available = Math.Min(
                TradeWorkers - _upcomingTrades.Values.Count(trade => !trade.Work.IsCompleted),
                RetainedTrades - _upcomingTrades.Count
            );
            if (available <= 0)
            {
                return;
            }
            HashSet<int> systems = [_locations[head.PilotId], .. _upcomingTrades.Values.Select(trade => trade.Input.SystemId)];
            foreach (var job in _queue.DueJobs(activity.ElapsedTicks).Take(TradeLookAhead))
            {
                if (
                    job.Action != CapsuleerAction.Trade
                    || _resuming.ContainsKey(job.PilotId)
                    || _upcomingTrades.ContainsKey(job)
                    || !systems.Add(_locations[job.PilotId])
                )
                {
                    continue;
                }
                try
                {
                    var input = WorldActionScope.Inspect(database, world => CapturePreparation(world, job, activity.ActiveSystems));
                    // The worker owns its catalog context. An AsyncLocal world owner or session scope cannot cross threads.
                    using var flow = ExecutionContext.IsFlowSuppressed() ? (AsyncFlowControl?)null : ExecutionContext.SuppressFlow();
                    var work = Task.Run(() => PlanUpcomingTrade(input, cancellationToken));
                    _upcomingTrades.Add(job, new(input, work));
                    TradesStartedAhead++;
                }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException or KeyNotFoundException)
                {
                    // Only the due job owns rejection and retry.
                }
                if (--available == 0)
                {
                    break;
                }
            }
        }
    }

    private CapsuleerHauler.PreparedJourney? PlanUpcomingTrade(PreparationInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using CatalogDbContext catalog = new(database.CatalogPath);
        PrepareStationInputs(catalog, input);
        if (input.PrepareInstitutionalOffers)
        {
            market.PrepareInstitutional(catalog, input.StationId);
        }
        token.ThrowIfCancellationRequested();
        return input.Journey is null ? null : CapsuleerHauler.PlanJourney(catalog, input.Journey, database.Settings.Capsuleers);
    }

    private bool TryPreparedTrade(PreparationInput current, out CapsuleerHauler.PreparedJourney? journey)
    {
        journey = null;
        lock (_tradeGate)
        {
            if (!_upcomingTrades.TryGetValue(current.Job, out var trade) || !trade.Work.IsCompleted)
            {
                return false;
            }
            _upcomingTrades.Remove(current.Job);
            if (!trade.Work.IsCompletedSuccessfully)
            {
                _ = trade.Work.Exception;
                return false;
            }
            var old = trade.Input;
            if (
                old.SystemId != current.SystemId
                || old.Catalog != current.Catalog
                || old.ShipId != current.ShipId
                || old.StationId != current.StationId
                || old.TrainingHull != current.TrainingHull
                || old.DefaultProfession != current.DefaultProfession
                || old.PrepareInstitutionalOffers != current.PrepareInstitutionalOffers
                || old.Journey != current.Journey
                || !SameTemplate(old.Template, current.Template)
            )
            {
                return false;
            }
            journey = trade.Work.Result;
            TradesUsedAhead++;
            return true;
        }
    }

    private static bool SameTemplate(TrafficTemplateInput? before, TrafficTemplateInput? after) =>
        before is null
            ? after is null
            : after is not null
                && before.Revision == after.Revision
                && before.System == after.System
                && before.Route == after.Route
                && before.Correction == after.Correction
                && before.Cargo == after.Cargo
                && before.Drones == after.Drones
                && before.Roster.SequenceEqual(after.Roster);

    public void Dispose()
    {
        UpcomingTrade[] workers;
        lock (_tradeGate)
        {
            _disposed = true;
            workers = _upcomingTrades.Values.ToArray();
            _upcomingTrades.Clear();
        }
        foreach (var worker in workers)
        {
            try
            {
                worker.Work.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                if (error is not (InvalidDataException or OperationCanceledException))
                {
                    ServerLog.Console.Warning($"Trade preparation ended during shutdown: {error.Message}");
                }
            }
        }
    }
}
