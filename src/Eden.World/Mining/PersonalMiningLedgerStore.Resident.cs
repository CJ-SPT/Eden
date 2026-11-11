using Eden.Data.World.Characters;
using Eden.Data.World.Mining;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

internal sealed partial class PersonalMiningLedgerStore
{
    internal static void Record(WorldAction world, Pilot pilot, WorldAsteroid resource, long now, MiningLedgerQuantity mined)
    {
        var quantity = MiningLedgerRules.Add(new(0, 0, 0), mined);
        if (quantity.Collected == 0 && quantity.Wasted == 0)
        {
            return;
        }
        long day = MiningLedgerRules.Day(now);
        var saved = world
            .Set<SavedPersonalMiningLog>()
            .SingleOrDefault(l =>
                l.PilotId == pilot.Id
                && l.Day == day
                && l.CorporationId == pilot.CorporationId
                && l.SystemId == resource.SystemId
                && l.TypeId == resource.TypeId
            );
        if (saved is null)
        {
            // Retire only this pilot's old daily buckets, when a new bucket is first written.
            // Repeated cycles update one row instead of retaining a per-cycle history.
            long first = MiningLedgerRules.EarliestDay(now);
            world.RemoveRange(world.Set<SavedPersonalMiningLog>().Where(l => l.PilotId == pilot.Id && l.Day < first));
            saved = world
                .Add(
                    new SavedPersonalMiningLog
                    {
                        PilotId = pilot.Id,
                        Day = day,
                        CorporationId = pilot.CorporationId,
                        SystemId = resource.SystemId,
                        TypeId = resource.TypeId,
                    }
                )
                .Entity;
        }
        var total = MiningLedgerRules.Add(new(saved.Quantity, saved.Critical, saved.Wasted), quantity);
        saved.Quantity = total.Collected;
        saved.Critical = total.Critical;
        saved.Wasted = total.Wasted;
    }
}
