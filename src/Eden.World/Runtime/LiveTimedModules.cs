using Eden.Data.World.Fittings;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

// Timed bonuses expire under the owner before reads, independently of socket frames.
internal sealed class LiveTimedModules(WorldDatabase database, int? system = null)
{
    private readonly ResidentSnapshot<int, long> _deadline = new("resident.timedModules", "WorldActiveModule", "schema");

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes) => _deadline.Observe(changes);

    internal void Expire(long now)
    {
        if (!_deadline.TryRead(0, 0, out long due))
        {
            using WorldDbContext world = new(database.Path) { IsResidentProjection = true };
            var active = world.Set<ActiveFittedModule>().Where(m => m.ExpiresAt > 0);
            if (system is int id)
            {
                var members = ResidentSystemMembership.Ships(world, database, id);
                active = active.Where(m => members.Contains(m.ShipId));
            }
            due = active.Min(m => (long?)m.ExpiresAt) ?? long.MaxValue;
            _deadline.Publish(0, due, 0, long.MaxValue);
        }
        if (due > now)
        {
            return;
        }
        long[] ships;
        using (WorldDbContext selection = new(database.Path) { IsResidentProjection = true })
        {
            var active = selection.Set<ActiveFittedModule>().Where(m => m.ExpiresAt > 0 && m.ExpiresAt <= now);
            if (system is int id)
            {
                var members = ResidentSystemMembership.Ships(selection, database, id);
                active = active.Where(m => members.Contains(m.ShipId));
            }
            ships = active.Select(m => m.ShipId).Distinct().ToArray();
        }
        using WorldDbContext expiry = new(database.Path) { Participants = new(ships: ships) };
        using var transaction = expiry.Database.BeginTransaction();
        expiry.Set<ActiveFittedModule>().Where(m => ships.Contains(m.ShipId) && m.ExpiresAt > 0 && m.ExpiresAt <= now).ExecuteDelete();
        transaction.Commit();
    }
}
