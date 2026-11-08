using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;

namespace Eden.Protocol.Flight;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerScene(SharedSpace space, TimeProvider clock, SharedStationGuests guests) : ICapsuleerScene
{
    public int Stamp => space.Stamp(clock);
    private ShipContact? _previous;
    private WarpFrame? _warp;
    private int _lastWorkStamp;
    private int _miningDeadline;
    private MiningContact? _mining;

    public void Publish(MinerObservation observation)
    {
        var (pilot, work, laser, _) = observation;
        guests.Update(this, pilot);
        lock (space.MotionLock)
        {
            if (_previous is { } old && (pilot.Position is null || pilot.ShipId != old.Pilot.ShipId))
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
            bool resumed = _previous is not null && Math.Abs((stamp - _previous.Stamp) - (work.Stamp - _lastWorkStamp)) > 1;
            if (resumed)
            {
                _warp = null;
                _mining = null;
            }
            _lastWorkStamp = work.Stamp;
            var nav = work.Navigation;
            var warp = nav.Warp is { } w
                ? w == _warp
                    ? _previous?.Warp
                    : new WarpCourse(w.Origin, w.Destination, stamp - (work.Stamp - w.StartStamp), pilot.Flight, w.EntrySpeed)
                : null;
            _warp = nav.Warp;
            if (work.Activity == MinerActivity.Mining && laser is not null)
            {
                if (_mining is null || _miningDeadline != work.DueStamp)
                {
                    long elapsed = TimeSpan.FromSeconds(work.CycleSeconds - (work.DueStamp - work.Stamp)).Ticks;
                    _mining = new(
                        laser.Id,
                        laser.TypeId,
                        work.TargetId,
                        clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() - elapsed,
                        TimeSpan.FromSeconds(work.CycleSeconds).TotalMilliseconds
                    );
                    _miningDeadline = work.DueStamp;
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
                stamp - (work.Stamp - nav.AlignStamp),
                (int)Math.Round(pilot.Flight.WarpSpeed * 1000),
                nav.FollowTarget?.Id ?? 0,
                Modules: laser is null ? [] : [new(laser.Id, laser.TypeId, laser.FlagId)],
                Mining: _mining is null ? [] : [_mining],
                Health: observation.Health,
                FollowMode: nav.FollowMode,
                Stamp: stamp
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
