using Eden.Data.World.Fittings;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

// Timed bonuses expire under the owner before reads, independently of socket frames.
internal sealed class LiveTimedModules(WorldDatabase database)
{
    private readonly ResidentSnapshot<int, long> _deadline = new("resident.timedModules", "WorldActiveModule", "schema");

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes) => _deadline.Observe(changes);

    internal void Expire(long now)
    {
        if (!_deadline.TryRead(0, 0, out long due))
        {
            using WorldDbContext world = new(database.Path);
            due = world.Set<ActiveFittedModule>().Where(m => m.ExpiresAt > 0).Min(m => (long?)m.ExpiresAt) ?? long.MaxValue;
            _deadline.Publish(0, due, 0, long.MaxValue);
        }
        if (due > now)
        {
            return;
        }
        long[] ships;
        using (WorldDbContext selection = new(database.Path))
        {
            ships = selection
                .Set<ActiveFittedModule>()
                .Where(m => m.ExpiresAt > 0 && m.ExpiresAt <= now)
                .Select(m => m.ShipId)
                .Distinct()
                .ToArray();
        }
        using WorldDbContext expiry = new(database.Path) { Participants = new(ships: ships) };
        using var transaction = expiry.Database.BeginTransaction();
        expiry.Set<ActiveFittedModule>().Where(m => m.ExpiresAt > 0 && m.ExpiresAt <= now).ExecuteDelete();
        transaction.Commit();
    }
}
