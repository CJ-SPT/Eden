using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;

namespace Eden.Server.Persistence;

internal sealed partial class LiveCombatRuntime
{
    // The motion operation calls this after its commit, while its connection still
    // owns the world. Other operations use conservative row invalidation on close.
    internal IReadOnlyCollection<CombatRowChange> Synchronize(WorldDbContext world, IReadOnlyCollection<CombatRowChange> changes)
    {
        List<CombatRowChange> consumed = [];
        if (_internal != 0 || changes.Count == 0)
        {
            return consumed;
        }
        foreach (var entry in world.ChangeTracker.Entries<WorldNpc>())
        {
            var row = entry.Entity;
            CombatRowChange key = new("WorldNpc", row.Id);
            if (
                !changes.Contains(key)
                || _staleNpcs.Contains(row.Id)
                || !_npcs.TryGetValue(row.Id, out var npc)
                || npc.TypeId != row.TypeId
            )
            {
                continue;
            }
            var defense = npc.Health.Defense;
            var next = npc with
            {
                Position = NpcMotion.Position(row),
                Velocity = NpcMotion.Velocity(row),
                Health = new(
                    defense,
                    new(
                        row.ShieldCharge,
                        Math.Max(0, defense.Armor - row.ArmorDamage),
                        Math.Max(0, defense.Structure - row.StructureDamage)
                    ),
                    row.UpdatedAt
                ),
                Available = row.DestroyedAt is null && row.RetiredAt is null,
            };
            _npcs[row.Id] = next;
            _savedNpcs[row.Id] = next;
            _staleNpcs.Remove(row.Id);
            _contactEpoch++;
            consumed.Add(key);
        }
        foreach (var entry in world.ChangeTracker.Entries<FlightPosition>())
        {
            var row = entry.Entity;
            CombatRowChange key = new("WorldPosition", row.ShipId);
            if (changes.Contains(key) && _shipPilots.TryGetValue(row.ShipId, out long pilot) && _ships.TryGetValue(pilot, out var ship))
            {
                var next = ship with { Position = new SpacePosition(row.X, row.Y, row.Z), SystemId = row.SystemId ?? ship.SystemId };
                _ships[pilot] = next;
                _savedShips[pilot] = next;
                _acquisitions.Clear();
                consumed.Add(key);
            }
        }
        return consumed;
    }
}
