using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
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
        bool PrepareInstitutionalOffers
    );

    internal PreparationInput? CapturePreparation()
    {
        using var phase = SessionPerformance.Phase("traffic.prepareCapture");
        using var owner = synchronization.Enter("traffic preparation capture");
        Load();
        var activity = presence.Read();
        Reconcile(activity);
        if (activity.Active)
        {
            StartBackgroundPilots(activity.ElapsedTicks);
        }
        var job = _queue.NextJob;
        if (
            job is null
            || _resuming.ContainsKey(job.PilotId)
            || _queue.NextDueTicks > activity.ElapsedTicks
            || job.Action
                is not (CapsuleerAction.Trade or CapsuleerAction.GateTransfer or CapsuleerAction.Arrival or CapsuleerAction.Prepare)
        )
        {
            return null;
        }
        try
        {
            return WorldActionScope.Inspect(database, world => CapturePreparation(world, job, activity.ActiveSystems));
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
        if (
            work?.TargetId is > 0 and <= int.MaxValue
            && catalog.Definitions.Gates.TryGetValue((int)work.TargetId, out var gate)
            && (occupied.Contains(gate.DestinationSystemId) || CanRunUnobserved(job.PilotId))
        )
        {
            systems.Add(gate.DestinationSystemId);
        }
        if (work?.TrafficLane is { } lane && occupied.Contains(lane.ObserverSystemId))
        {
            systems.Add(lane.ObserverSystemId);
        }
        bool detailed = ActiveOperation(job.PilotId);
        var geometry = systems
            .Where(id => detailed || occupied.Contains(id))
            .Select(id => CapsuleerGeometry.CaptureResources(database, world, catalog, id))
            .OfType<CapsuleerGeometry.ResourcePreparation>()
            .ToArray();
        var missing = geometry.SelectMany(capture => capture.Missing).Distinct().ToArray();
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
            stationId != 0
                && (job.Action is CapsuleerAction.Trade or CapsuleerAction.Prepare)
                && PilotMarketStore.UsesInstitutionalOffers(world)
        );
    }

    internal bool PreparationCurrent(PreparationInput input)
    {
        var activity = presence.Read();
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

    private CapsuleerBatch RunPrepared(int destinyStamp, int maximumActions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumActions);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumActions, CapsuleerJobQueue.ActionsPerTurn);
        // Legacy synchronous callers may already own the world; they retain the safe
        // in-owner fallback. Production maintenance enters here without ownership.
        if (synchronization.IsEntered)
        {
            return RunCore(destinyStamp, maximumActions);
        }
        long started = clock.GetTimestamp();
        var input = CapturePreparation();
        using var measurement = input is null
            ? null
            : Live?.TrafficActions.Measure(input.Job, input.Systems, () => (Live!.CheckpointCopies, Live.CheckpointCopying));
        Dictionary<int, AsteroidFieldStore.PreparedBelt> belts = [];
        CapsuleerHauler.PreparedJourney? journey = null;
        Dictionary<int, IReadOnlyList<StaticSpaceObject>> geometry = [];
        if (input is not null)
        {
            try
            {
                using var phase = SessionPerformance.Phase("traffic.preparation");
                using CatalogDbContext catalog = new(database.CatalogPath);
                if (input.Template is not null)
                {
                    TrafficTemplatePreparation.Prepare(database, catalog, input.Template);
                }
                foreach (int system in input.Systems)
                {
                    var objects = SystemObjects.Geometry(catalog, system);
                    if (input.Geometry.FirstOrDefault(capture => capture.System == system)?.Resources is { } resources)
                    {
                        geometry[system] = Array.AsReadOnly<StaticSpaceObject>([.. objects, .. resources]);
                    }
                }
                _ = CapsuleerTrafficRoutes.Read(catalog, input.CommunityId);
                if (input.Journey is not null)
                {
                    journey = CapsuleerHauler.PlanJourney(catalog, input.Journey);
                }
                foreach (int belt in input.MissingBelts)
                {
                    belts[belt] = AsteroidFieldStore.Prepare(catalog, belt);
                }
                if (input.PrepareInstitutionalOffers)
                {
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
            catch (Exception error)
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
        }
        using var owner = synchronization.Enter("traffic maintenance");
        if (input is not null && !PreparationCurrent(input))
        {
            long? oldest = _queue.NextDueTicks is { } due && due <= presence.Read().ElapsedTicks ? due : null;
            return new(new(0, Current.Count, oldest, false), [], []);
        }
        if (input is not null && Live is { } live)
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
        return RunCore(destinyStamp, maximumActions, preparedJourney: journey, measured: input is not null, turnStarted: started);
    }
}
