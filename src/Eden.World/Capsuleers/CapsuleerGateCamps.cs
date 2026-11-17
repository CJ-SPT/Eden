using System.Collections.Concurrent;
using Eden.Gameplay.Capsuleers;

namespace Eden.World.Capsuleers;

// One current gate and three role slots per visited system. All operations on one camp are serialized by its owner.
internal sealed class CapsuleerGateCamps
{
    internal sealed class Watch(long gate, long until)
    {
        private readonly long[] _members = new long[3];
        internal long Gate { get; } = gate;
        internal long Until { get; } = until;
        internal bool Dispersing { get; set; }
        private long _reportedShip;
        private long _reportExpires;

        internal bool Member(long pilot) => Array.IndexOf(_members, pilot) >= 0;

        internal CampRole? Position(long pilot, bool tackle, CampRole? saved = null)
        {
            int slot = Array.IndexOf(_members, pilot);
            if (slot >= 0)
            {
                return (CampRole)slot;
            }
            if (Dispersing)
            {
                return null;
            }
            var role =
                saved
                ?? (
                    tackle && _members[(int)CampRole.Tackle] == 0 ? CampRole.Tackle
                    : _members[(int)CampRole.Scout] == 0 ? CampRole.Scout
                    : CampRole.Damage
                );
            if (_members[(int)role] != 0)
            {
                return null;
            }
            return role;
        }

        internal CampRole? Join(long pilot, bool tackle, CampRole? saved = null)
        {
            var role = Position(pilot, tackle, saved);
            if (role is not null)
            {
                _members[(int)role.Value] = pilot;
            }
            return role;
        }

        internal void Leave(long pilot)
        {
            int slot = Array.IndexOf(_members, pilot);
            if (slot >= 0)
            {
                _members[slot] = 0;
            }
        }

        internal long Reported(long elapsed) => elapsed < _reportExpires ? _reportedShip : 0;

        internal void Report(long shipId, long elapsed)
        {
            _reportedShip = shipId;
            _reportExpires = elapsed + TimeSpan.FromSeconds(12).Ticks;
        }
    }

    private readonly ConcurrentDictionary<int, Watch> _watches = new();

    internal Watch? At(int system, long gate, long until, long elapsed)
    {
        if (until <= elapsed)
        {
            return null;
        }
        var current = _watches.GetOrAdd(system, static (_, key) => new(key.gate, key.until), (gate, until));
        if (current.Until <= elapsed)
        {
            var next = new Watch(gate, until);
            current = _watches.TryUpdate(system, next, current) ? next : _watches[system];
        }
        return current.Until == until && current.Gate == gate ? current : null;
    }
}
