using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Docking;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private sealed record InboundCadence(int Gates, int Stations);

    private readonly Dictionary<int, InboundCadence> _inboundDwells = [];

    internal bool IsInbound => _loaded && Work.Activity == HaulerActivity.Inbound;

    private void EnsureCurrentHull()
    {
        if (!_loaded || Live is { } live && _hullRevision != live.CapsuleerHullRevisions.GetValueOrDefault(pilotId))
        {
            Load();
            _hullRevision = Live?.CapsuleerHullRevisions.GetValueOrDefault(pilotId) ?? 0;
        }
    }

    private void LoadInboundDwells(CatalogDbContext catalog)
    {
        if (Work.TrafficLane is not { } lane)
        {
            return;
        }
        foreach (int system in new[] { lane.ObserverSystemId, lane.SourceSystemId })
        {
            _inboundDwells[system] = new(
                catalog.Definitions.GatesInSystem(system).Length,
                catalog.Definitions.StationsInSystem(system).Length
            );
        }
    }

    internal CapsuleerJob ArmInbound(CapsuleerJob job, long elapsed, WatchedTrafficGate gate)
    {
        EnsureCurrentHull();
        if (
            Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup
            || Pilot.SystemId != gate.SourceSystemId
            || Work.Activity == HaulerActivity.Jumping
        )
        {
            return job;
        }
        if (Work.Activity == HaulerActivity.Inbound && !Work.InboundPaused)
        {
            return job;
        }
        long delay = Math.Max(0, elapsed - Work.ActiveTicks);
        var leg = HaulingRouteRules.Crossing(_route.Systems, gate.SourceSystemId, gate.ObserverSystemId);
        var cadence = _inboundDwells[gate.ObserverSystemId];
        int dwell = HaulingRules.InboundDwellSeconds(cadence.Gates, cadence.Stations, pilotId, Work.DwellSequence);
        long due = Work.Activity == HaulerActivity.Inbound ? Work.DueTicks + delay : elapsed + TimeSpan.FromSeconds(dwell).Ticks;
        NavigationState navigation = new();
        var work = Work with
        {
            Activity = HaulerActivity.Inbound,
            ActiveTicks = elapsed,
            ClockOffsetTicks = Work.ClockOffsetTicks + delay,
            DueTicks = due,
            DueStamp = Work.Activity == HaulerActivity.Inbound ? Work.DueStamp : Work.Stamp + dwell,
            TargetId = gate.DepartureGateId,
            RouteIndex = leg.Index,
            RouteDirection = leg.Direction,
            Navigation = navigation.Capture(),
            TrafficLane = new(gate.ObserverSystemId, gate.SourceSystemId, gate.ArrivalGateId, gate.DepartureGateId),
            LaneDeparting = true,
            InboundPaused = false,
            WaitingReason = null,
            ArrivalGateId = null,
            ArrivalAt = null,
            DepartureAt = null,
            DwellSequence = Work.Activity == HaulerActivity.Inbound ? Work.DwellSequence : checked(Work.DwellSequence + 1),
        };
        var next = job.Next(Math.Max(elapsed + TimeSpan.TicksPerSecond, due), CapsuleerAction.GateTransfer);
        using CapsuleerOperation operation = new(database, job, marketSignal);
        operation.Commit(next, null, new HaulerCheckpoint(pilotId, work));
        _work = work;
        _navigation = navigation;
        return next;
    }

    internal CapsuleerJob PauseInbound(CapsuleerJob job, long elapsed)
    {
        if (!IsInbound || Work.InboundPaused)
        {
            return job;
        }
        long due = Math.Max(elapsed + TimeSpan.TicksPerSecond, Work.DueTicks);
        var work = Work with
        {
            ActiveTicks = elapsed,
            Stamp = CapsuleerScheduleRules.Stamp(elapsed, Work.ClockOffsetTicks),
            DueTicks = due,
            InboundPaused = true,
        };
        var next = job.Next(due, CapsuleerAction.GateTransfer);
        using CapsuleerOperation operation = new(database, job, marketSignal);
        operation.Commit(next, null, new HaulerCheckpoint(pilotId, work));
        _work = work;
        return next;
    }

    internal CapsuleerJob ReleaseInbound(CapsuleerJob job, long elapsed)
    {
        EnsureCurrentHull();
        if (!IsInbound)
        {
            return job;
        }
        var work = Work with
        {
            Activity = Pilot.Position is null ? HaulerActivity.Docked : HaulerActivity.Waiting,
            ActiveTicks = elapsed,
            Stamp = CapsuleerScheduleRules.Stamp(elapsed, Work.ClockOffsetTicks),
            DueTicks = elapsed + TimeSpan.TicksPerSecond,
            InboundPaused = false,
            WaitingReason = null,
            ArrivalGateId = null,
            ArrivalAt = null,
            DepartureAt = null,
        };
        var next = job.Next(work.DueTicks, Pilot.Position is null ? CapsuleerAction.Trade : CapsuleerAction.Retry);
        using CapsuleerOperation operation = new(database, job, marketSignal);
        operation.Commit(next, null, new HaulerCheckpoint(pilotId, work));
        _work = work;
        return next;
    }

    private void CompleteInbound()
    {
        if (Work.InboundPaused || Work.TrafficLane is not { } lane || Pilot.SystemId != lane.SourceSystemId)
        {
            throw new InvalidDataException("The inbound gate encounter is no longer ready.");
        }
        if (Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup)
        {
            throw new InvalidDataException("Waiting for a replacement hauling ship.");
        }
        if (_warpBlocked)
        {
            throw new InvalidDataException("Warp disruption prevents the inbound hauler from escaping.");
        }
        var saved = _operation.World.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        if (saved.ActiveShipId != Work.ShipId)
        {
            throw new InvalidDataException("The inbound hauling ship is no longer active.");
        }
        Eden.World.Combat.CrimewatchPersistence.CheckTravel(_operation.World, _operation.Catalog, saved, CrimewatchTravel.Warp, Now);
        var departure =
            _objects.SingleOrDefault(o =>
                o.Id == lane.DepartureGateId && o.DestinationGateId == lane.ArrivalGateId && o.DestinationSystemId == lane.ObserverSystemId
            ) ?? throw new InvalidDataException("The inbound hauling route has no reciprocal stargate.");
        var position =
            departure.Position + new SpacePosition(departure.Radius + Pilot.Flight.ShipRadius + CapsuleerTrafficFlight.ApproachRange, 0, 0);
        SqliteWorld.SetPosition(_operation.World, _operation.Catalog, Pilot.ShipId, position, Pilot.SystemId, Now);
        _target = departure;
        var leg = HaulingRouteRules.Crossing(_route.Systems, Pilot.SystemId, lane.ObserverSystemId);
        _work = Work with { TargetId = departure.Id, RouteIndex = leg.Index, RouteDirection = leg.Direction, InboundPaused = false };
        CompleteJump();
    }
}
