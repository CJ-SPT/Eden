using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;

namespace Eden.Protocol.Flight;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerScene(SharedSpace space, TimeProvider clock, SharedStationGuests guests) : ICapsuleerScene
{
    private readonly Dictionary<long, PilotScene> _pilots = [];
    private readonly Dictionary<long, object> _presence = [];
    private long _revision;
    public int Stamp => space.Stamp(clock);
    private int _materializedPilots;
    public int MaterializedPilots => Volatile.Read(ref _materializedPilots);

    public void Apply(CapsuleerBatch batch)
    {
        lock (space.MotionLock)
        {
            if (batch.Revision <= _revision)
            {
                return;
            }
            var active = batch.Systems.Where(s => s.Mode == SystemSimulationMode.Active).Select(s => s.SystemId).ToHashSet();
            foreach (long id in _pilots.Where(p => !active.Contains(p.Value.SystemId)).Select(p => p.Key).ToArray())
            {
                _pilots[id].Leave();
                _pilots.Remove(id);
            }
            foreach (var observation in batch.Miners)
            {
                Presence(observation.Pilot);
                if (active.Contains(observation.Pilot.SystemId))
                {
                    Publish(observation);
                }
                else
                {
                    Remove(observation.Pilot.PilotId);
                }
            }
            foreach (var observation in batch.Haulers)
            {
                Presence(observation.Pilot);
                if (active.Contains(observation.Pilot.SystemId))
                {
                    Publish(observation);
                }
                else
                {
                    Remove(observation.Pilot.PilotId);
                }
            }
            Volatile.Write(ref _materializedPilots, _pilots.Count);
            _revision = batch.Revision;
        }
    }

    private void Presence(DockedPilot pilot)
    {
        if (!_presence.TryGetValue(pilot.PilotId, out var key))
        {
            key = new object();
            _presence[pilot.PilotId] = key;
        }
        guests.Update(key, pilot);
    }

    private void Remove(long pilotId)
    {
        if (_pilots.Remove(pilotId, out var previous))
        {
            previous.Leave();
        }
    }

    public void Publish(MinerObservation observation) =>
        Publish(
            new Visual(
                observation.Pilot,
                observation.Work.Navigation,
                observation.Work.Stamp,
                observation.Health,
                observation.Work.Activity == MinerActivity.Mining,
                observation.Laser,
                observation.Work.TargetId,
                observation.Work.DueStamp,
                observation.Work.CycleSeconds
            )
        );

    public void Publish(HaulerObservation observation) =>
        Publish(
            new Visual(
                observation.Pilot,
                observation.Work.Navigation,
                observation.Work.Stamp,
                observation.Health,
                DepartureGate: observation.Work.Activity == HaulerActivity.Jumping ? observation.Work.TargetId : null,
                Arriving: observation.Work.Stamp < observation.Work.ArrivalUntil,
                GateArrival: observation.Work is { ArrivalGateId: long gate, ArrivalAt: long arrived } ? new(gate, arrived) : null,
                DepartureAt: observation.Work.DepartureAt
            )
        );

    private void Publish(Visual observation)
    {
        lock (space.MotionLock)
        {
            if (!_pilots.TryGetValue(observation.Pilot.PilotId, out var pilot))
            {
                pilot = new(space, clock);
                _pilots.Add(observation.Pilot.PilotId, pilot);
            }
            pilot.Publish(observation);
            Volatile.Write(ref _materializedPilots, _pilots.Count);
        }
    }

    private sealed record Visual(
        DockedPilot Pilot,
        NavigationFrame Navigation,
        int Stamp,
        ShipHealth? Health,
        bool Mining = false,
        InventoryItem? Laser = null,
        long TargetId = 0,
        int DueStamp = 0,
        int CycleSeconds = 0,
        long? DepartureGate = null,
        bool Arriving = false,
        GateArrivalContact? GateArrival = null,
        long? DepartureAt = null
    );

    private sealed class PilotScene(SharedSpace space, TimeProvider clock)
    {
        internal int SystemId { get; private set; }

        internal void Leave()
        {
            if (_previous is { } previous)
            {
                space.Leave(previous.Pilot.ShipId);
            }
        }

        private ShipContact? _previous;
        private WarpFrame? _warp;
        private int _lastWorkStamp;
        private int _miningDeadline;
        private MiningContact? _mining;

        public void Publish(Visual observation)
        {
            var pilot = observation.Pilot;
            var laser = observation.Laser;
            SystemId = pilot.SystemId;
            lock (space.MotionLock)
            {
                if (
                    _previous is { } old
                    && (pilot.Position is null || pilot.ShipId != old.Pilot.ShipId || pilot.SystemId != old.Pilot.SystemId)
                )
                {
                    space.Leave(old.Pilot.ShipId);
                    _previous = null;
                    _warp = null;
                    _mining = null;
                }
                if (pilot.Position is null)
                {
                    return;
                }
                int stamp = space.Stamp(clock);
                bool resumed = _previous is not null && Math.Abs((stamp - _previous.Stamp) - (observation.Stamp - _lastWorkStamp)) > 1;
                if (resumed)
                {
                    _warp = null;
                    _mining = null;
                }
                _lastWorkStamp = observation.Stamp;
                var nav = observation.Navigation;
                var warp = nav.Warp is { } w
                    ? w == _warp
                        ? _previous?.Warp
                        : new WarpCourse(w.Origin, w.Destination, stamp - (observation.Stamp - w.StartStamp), pilot.Flight, w.EntrySpeed)
                    : null;
                _warp = nav.Warp;
                if (observation.Mining && laser is not null)
                {
                    if (_mining is null || _miningDeadline != observation.DueStamp)
                    {
                        long elapsed = TimeSpan.FromSeconds(observation.CycleSeconds - (observation.DueStamp - observation.Stamp)).Ticks;
                        _mining = new(
                            laser.Id,
                            laser.TypeId,
                            observation.TargetId,
                            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() - elapsed,
                            TimeSpan.FromSeconds(observation.CycleSeconds).TotalMilliseconds
                        );
                        _miningDeadline = observation.DueStamp;
                    }
                }
                else
                {
                    _mining = null;
                }
                var contact = new ShipContact(
                    PilotContact.From(pilot),
                    nav.Velocity,
                    nav.Direction,
                    nav.SpeedFraction,
                    nav.FollowTarget is null ? null : nav.FollowRange,
                    warp,
                    nav.WarpDestination,
                    stamp - (observation.Stamp - nav.AlignStamp),
                    (int)Math.Round(pilot.Flight.WarpSpeed * 1000),
                    nav.FollowTarget?.Id ?? 0,
                    DepartureGate: observation.DepartureGate,
                    Arriving: observation.Arriving,
                    Modules: laser is null ? [] : [new(laser.Id, laser.TypeId, laser.FlagId)],
                    Mining: _mining is null ? [] : [_mining],
                    Health: observation.Health,
                    FollowMode: nav.FollowMode,
                    Stamp: stamp,
                    GateArrival: observation.GateArrival,
                    DepartureAt: observation.DepartureAt
                );
                long id = pilot.ShipId;
                if (contact.AlignTo is { } end && _previous?.AlignTo != end)
                {
                    Order(new DestinyCommand.SetSpeedFraction(id, 1));
                    Order(new DestinyCommand.WarpTo(id, end.X, end.Y, end.Z, 0, contact.WarpFactor));
                }
                else if (nav.FollowTarget is { } target && contact.FollowTargetId != _previous?.FollowTargetId)
                {
                    Order(new DestinyCommand.Follow(nav.FollowMode, id, target.Id, nav.FollowRange));
                }
                else if (nav.Direction is null && warp is null && _previous?.Heading is not null)
                {
                    Order(new DestinyCommand.Stop(id));
                }
                space.Publish(contact);
                _previous = contact;

                void Order(DestinyCommand command) => space.RecordOrder(id, new(stamp, command));
            }
        }
    }
}
