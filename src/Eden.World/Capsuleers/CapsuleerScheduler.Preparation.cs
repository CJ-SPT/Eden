using System.Collections.Concurrent;
using System.Diagnostics;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private long _preparedTravelCatalog;
    private readonly ConcurrentDictionary<int, long> _preparedPopulationCatalog = [];

    internal long RoutineArrivals { get; private set; }
    internal long PreparedArrivals { get; private set; }

    internal sealed record PreparationInput(
        CapsuleerJob Job,
        int SystemId,
        int StationId,
        int CommunityId,
        long ShipId,
        long Revision,
        long Resources,
        long Catalog,
        IReadOnlyList<int> Systems,
        IReadOnlyList<int> MissingBelts,
        IReadOnlyList<CapsuleerGeometry.ResourcePreparation> Geometry,
        CapsuleerHauler.JourneyInput? Journey,
        TrafficTemplateInput? Template,
        int TrainingHull,
        CapsuleerProfession DefaultProfession,
        bool PrepareMercoxit,
        bool PrepareInstitutionalOffers
    );

    internal PreparationInput? CapturePreparation(CancellationToken cancellationToken = default)
    {
        using var owner = synchronization.Enter("traffic preparation capture");
        cancellationToken.ThrowIfCancellationRequested();
        return CapturePreparation(PrepareMembership(), false, cancellationToken);
    }

    private HumanActivitySnapshot PrepareMembership()
    {
        Load();
        var activity = presence.Read();
        Reconcile(activity);
        if (activity.Active)
        {
            StartBackgroundPilots(activity.ElapsedTicks);
        }
        return activity;
    }

    private PreparationInput? CapturePreparation(HumanActivitySnapshot activity, bool lookAhead, CancellationToken cancellationToken)
    {
        using var phase = SessionPerformance.Phase("traffic.prepareCapture");
        var job = _queue.NextJob;
        if (!activity.Active || job is null || _resuming.ContainsKey(job.PilotId) || _queue.NextDueTicks > activity.ElapsedTicks)
        {
            return null;
        }
        if (
            job.Action is not (CapsuleerAction.Trade or CapsuleerAction.GateTransfer or CapsuleerAction.Arrival or CapsuleerAction.Prepare)
            && (
                npcPopulation is null
                || job.Action is not (CapsuleerAction.Collect or CapsuleerAction.Lock)
                || _registrations[job.PilotId].Career != CapsuleerCareer.Mining
                || _preparedPopulationCatalog.GetValueOrDefault(_locations[job.PilotId]) == database.Catalog.Identity.Generation
            )
        )
        {
            return null;
        }
        if (job.Action == CapsuleerAction.Arrival && Live is { } live)
        {
            int system = _locations[job.PilotId];
            // Arrival advances the current leg. Transfers and new journeys have their own jobs.
            // Cold or detailed roles can still need geometry prepared outside the owner.
            if (
                !activity.ActiveSystems.Contains(system)
                    && _haulers.TryGetValue(job.PilotId, out var traveler)
                    && traveler.HasFlightInputs(system, database.Catalog.Identity.Generation)
                    && !ActiveOperation(job.PilotId)
                || live.TrafficGeometry.TryRead(system, database.Catalog.Identity.Generation, out _)
                    && _miners.TryGetValue(job.PilotId, out var miner)
                    && miner.PreparationPilot?.SystemId == system
            )
            {
                RoutineArrivals++;
                return null;
            }
            PreparedArrivals++;
        }
        try
        {
            var captured = WorldActionScope.Inspect(database, world => CapturePreparation(world, job, activity.ActiveSystems));
            if (lookAhead && job.Action == CapsuleerAction.Trade)
            {
                StartUpcomingTrades(job, activity, cancellationToken);
            }
            return captured;
        }
        catch (Exception error)
        {
            RecordFailure(job, activity.ElapsedTicks, error);
            throw;
        }
    }

    private PreparationInput CapturePreparation(WorldAction world, CapsuleerJob job, IReadOnlyList<int> occupied)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var hauler = HaulerWorker(job.PilotId);
        var resident = hauler?.PreparationPilot ?? _miners.GetValueOrDefault(job.PilotId)?.PreparationPilot;
        long shipId;
        int stationId;
        if (resident is not null)
        {
            shipId = resident.ShipId;
            stationId = resident.StationId;
        }
        else
        {
            shipId = world.Pilots.Where(p => p.Id == job.PilotId).Select(p => p.ActiveShipId).Single();
            stationId = world.Ships.Where(ship => ship.Id == shipId).Select(ship => ship.StationId).Single();
        }
        int system = _locations[job.PilotId];
        HashSet<int> systems = [system];
        var work = Live?.HaulerCheckpoints.GetValueOrDefault(job.PilotId)?.Work;
        var expedition = Live?.MinerCheckpoints.GetValueOrDefault(job.PilotId)?.Work;
        long? gateTarget = work?.TargetId ?? (expedition?.Activity == MinerActivity.Jumping ? expedition.TargetId : null);
        if (
            gateTarget is > 0 and <= int.MaxValue
            && catalog.Definitions.Gates.TryGetValue((int)gateTarget, out var gate)
            && (occupied.Contains(gate.DestinationSystemId) || CanRunUnobserved(job.PilotId))
        )
        {
            systems.Add(gate.DestinationSystemId);
        }
        if (work?.TrafficLane is { } lane && occupied.Contains(lane.ObserverSystemId))
        {
            systems.Add(lane.ObserverSystemId);
        }
        bool detailed =
            ActiveOperation(job.PilotId) || _registrations[job.PilotId].Career == CapsuleerCareer.Mining || work?.Ratting is not null;
        var geometry = job.Action is CapsuleerAction.Collect or CapsuleerAction.Lock
            ? []
            : systems
                .Where(id => detailed || occupied.Contains(id))
                .Select(id => CapsuleerGeometry.CaptureResources(database, world, catalog, id))
                .OfType<CapsuleerGeometry.ResourcePreparation>()
                .ToArray();
        var missing = geometry.SelectMany(capture => capture.Missing).Distinct().ToArray();
        bool dockedWork = stationId != 0 && job.Action is CapsuleerAction.Trade or CapsuleerAction.Prepare;
        bool mercoxit =
            dockedWork
            && job.Action == CapsuleerAction.Prepare
            && !world.Settings.FreshUniverse
            && world.Settings.Capsuleers.Miners
            && world.Set<Eden.Data.World.Capsuleers.SavedCapsuleerAgent>().Find(job.PilotId) is { Slot: 0 } miner
            && miner.CommunitySystemId == system;
        return new(
            job,
            system,
            stationId,
            _registrations[job.PilotId].Community,
            shipId,
            Live?.PreparationRevision ?? 0,
            Live?.TrafficGeometry.Revision ?? 0,
            catalog.Definitions.Identity.Generation,
            Array.AsReadOnly(systems.Order().ToArray()),
            Array.AsReadOnly(missing),
            Array.AsReadOnly(geometry),
            hauler?.CaptureJourney(world, job),
            job.Action == CapsuleerAction.Trade && stationId != 0
                ? TrafficTemplatePreparation.Capture(
                    database,
                    world,
                    catalog,
                    job.PilotId,
                    shipId,
                    _registrations[job.PilotId].Community,
                    hauler?.PreparationRoute
                )
                : null,
            dockedWork ? resident?.ShipTypeId ?? world.Ships.Find(shipId)?.TypeId ?? 0 : 0,
            job.Action == CapsuleerAction.Prepare
                ? CapsuleerProfession.Mining
                : work?.CareerPlan?.Profession ?? CapsuleerProfession.Hauling,
            mercoxit,
            dockedWork && PilotMarketStore.UsesInstitutionalOffers(world)
        );
    }

    internal bool PreparationCurrent(PreparationInput input) => PreparationCurrent(input, presence.Read());

    private bool PreparationCurrent(PreparationInput input, HumanActivitySnapshot activity)
    {
        return activity.Active
            && Current.GetValueOrDefault(input.Job.PilotId) == input.Job
            && _queue.NextJob == input.Job
            && (CanRunUnobserved(input.Job.PilotId) || input.Systems.All(activity.ActiveSystems.Contains))
            && _locations.GetValueOrDefault(input.Job.PilotId) == input.SystemId
            && (Live?.PreparationRevision ?? 0) == input.Revision
            && (Live?.TrafficGeometry.Revision ?? 0) == input.Resources
            && (input.Template is null || input.Template.Revision == (Live?.TrafficTemplateRevision ?? 0))
            && database.Catalog.Identity.Generation == input.Catalog;
    }

    private void PrepareStationInputs(CatalogDbContext catalog, PreparationInput input)
    {
        if (input.Template is not null)
        {
            TrafficTemplatePreparation.Prepare(database, catalog, input.Template);
        }
        if (input.Job.Action == CapsuleerAction.Trade && (input.Template is not null || DroneRules.SupportsHull(input.TrainingHull)))
        {
            using var supplies = SessionPerformance.Phase("traffic.prepareDroneSupplies");
            _ = CapsuleerSupplyStore.AvailableDrones(catalog);
        }
        if (input.TrainingHull != 0)
        {
            var hull = (HullClass)catalog.RequireItem(input.TrainingHull).GroupId;
            var profession =
                input.Job.Action == CapsuleerAction.Prepare
                    ? CapsuleerProfession.Mining
                    : CapsuleerCareerTraining.Profession(catalog, input.TrainingHull, hull) ?? input.DefaultProfession;
            CapsuleerCareerTraining.PrepareChoices(catalog, input.TrainingHull, profession);
        }
    }

    private CapsuleerBatch RunPrepared(int destinyStamp, int maximumActions, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumActions);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumActions, CapsuleerJobQueue.ActionsPerTurn);
        cancellationToken.ThrowIfCancellationRequested();
        // Legacy synchronous callers may already own the world; they retain the safe
        // in-owner fallback. Production maintenance enters here without ownership.
        if (synchronization.IsEntered)
        {
            using var nested = synchronization.Enter("traffic maintenance");
            return RunCore(destinyStamp, maximumActions, PrepareMembership());
        }
        long started = clock.GetTimestamp();
        PreparationInput? input;
        using (synchronization.Enter("traffic maintenance"))
        {
            var activity = PrepareMembership();
            input = CapturePreparation(activity, true, cancellationToken);
            if (input is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return RunCore(destinyStamp, maximumActions, activity, turnStarted: started);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan? ownerWait = null;
        using var measurement = Live?.TrafficActions.Measure(
            input.Job,
            input.Systems,
            () => (Live!.CheckpointCopies, Live.CheckpointCopying),
            () => ownerWait
        );
        Dictionary<int, AsteroidFieldStore.PreparedBelt> belts = [];
        CapsuleerHauler.PreparedJourney? journey = null;
        Dictionary<int, IReadOnlyList<StaticSpaceObject>> geometry = [];
        try
        {
            using var phase = SessionPerformance.Phase("traffic.preparation");
            using CatalogDbContext catalog = new(database.CatalogPath);
            if (Volatile.Read(ref _preparedTravelCatalog) != input.Catalog)
            {
                using var travelCatalog = SessionPerformance.Phase("traffic.prepareTravelCatalog");
                catalog.Definitions.WarmGateBodies();
                npcPopulation?.WarmCatalog(database);
                Volatile.Write(ref _preparedTravelCatalog, input.Catalog);
            }
            bool preparedTrade = input.Job.Action == CapsuleerAction.Trade && TryPreparedTrade(input, out journey);
            if (!preparedTrade)
            {
                PrepareStationInputs(catalog, input);
            }
            if (input.PrepareMercoxit)
            {
                using var routes = SessionPerformance.Phase("traffic.prepareMercoxitRoutes");
                _ = CapsuleerTrafficRoutes.MercoxitRoutes(catalog.Definitions);
            }
            if (input.Job.Action == CapsuleerAction.Prepare && input.StationId != 0)
            {
                using var production = SessionPerformance.Phase("traffic.prepareManufacturing");
                CapsuleerIndustry.WarmManufacturing(catalog);
            }
            foreach (int system in input.Systems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    npcPopulation is not null
                    && input.Job.Action is CapsuleerAction.Prepare or CapsuleerAction.Collect or CapsuleerAction.Lock
                    && _preparedPopulationCatalog.GetValueOrDefault(system) != input.Catalog
                )
                {
                    using var population = SessionPerformance.Phase("traffic.prepareSystemPopulation");
                    NpcPopulationSeeder.WarmSystem(database, catalog, system);
                    _preparedPopulationCatalog[system] = input.Catalog;
                }
                if (input.Job.Action is CapsuleerAction.Collect or CapsuleerAction.Lock)
                {
                    continue;
                }
                var objects = Live?.TrafficGeometry.StaticObjects(catalog, system) ?? SystemObjects.Geometry(catalog, system);
                if (input.Geometry.FirstOrDefault(capture => capture.System == system)?.Resources is { } resources)
                {
                    geometry[system] = Array.AsReadOnly<StaticSpaceObject>([.. objects, .. resources]);
                }
            }
            if (input.Job.Action is CapsuleerAction.Trade or CapsuleerAction.Prepare)
            {
                _ = CapsuleerTrafficRoutes.Read(catalog, input.CommunityId);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!preparedTrade && input.Journey is not null)
            {
                journey = CapsuleerHauler.PlanJourney(catalog, input.Journey, database.Settings.Capsuleers);
            }
            foreach (int belt in input.MissingBelts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                belts[belt] = AsteroidFieldStore.Prepare(catalog, belt);
            }
            if (!preparedTrade && input.PrepareInstitutionalOffers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var institutional = SessionPerformance.Phase("traffic.institutionalOffers");
                market.PrepareInstitutional(catalog, input.StationId);
            }
        }
        catch (InvalidDataException)
        {
            // Optional preparation cannot change the role's established rejection/
            // waiting behavior. The authoritative action validates and reports it.
            belts.Clear();
            geometry.Clear();
            journey = null;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            using var failureOwner = synchronization.Enter("traffic preparation failure");
            // Another maintenance caller may have committed this job while its
            // optional plan was being built. Never penalize the replacement job.
            if (Current.GetValueOrDefault(input.Job.PilotId) == input.Job)
            {
                RecordFailure(input.Job, presence.Read().ElapsedTicks, error);
            }
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        long waiting = Stopwatch.GetTimestamp();
        using var owner = synchronization.Enter("traffic maintenance");
        ownerWait = Stopwatch.GetElapsedTime(waiting);
        cancellationToken.ThrowIfCancellationRequested();
        var currentActivity = PrepareMembership();
        if (!PreparationCurrent(input, currentActivity))
        {
            long? oldest = _queue.NextDueTicks is { } due && due <= presence.Read().ElapsedTicks ? due : null;
            return new(new(0, Current.Count, oldest, false), [], []);
        }
        if (Live is { } live)
        {
            foreach (var capture in input.Geometry)
            {
                if (geometry.TryGetValue(capture.System, out var objects))
                {
                    live.TrafficGeometry.Publish(capture.System, input.Catalog, capture.Belts, objects);
                }
            }
        }
        using var prepared = AsteroidFieldStore.UsePrepared(belts);
        return RunCore(destinyStamp, maximumActions, currentActivity, preparedJourney: journey, measured: true, turnStarted: started);
    }
}
