using Eden.Data.World.Characters;
using Eden.Data.World.Mining;
using Eden.Data.World.Structures;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

[Injectable]
internal sealed partial class PersonalMiningLedgerStore(WorldDatabase database, TimeProvider clock) : IPersonalMiningLedger
{
    public IReadOnlyList<PersonalMiningLog> Read(long pilotId) => WorldActionScope.Inspect(database, world => Read(world, pilotId));

    private IReadOnlyList<PersonalMiningLog> Read(WorldAction world, long pilotId)
    {
        if (!world.PilotExists(pilotId))
        {
            throw new InvalidDataException("Read the mining ledger for a known pilot.");
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        long first = MiningLedgerRules.EarliestDay(now);
        long today = MiningLedgerRules.Day(now);
        return world
            .Filter<SavedPersonalMiningLog>(l => l.PilotId == pilotId && l.Day >= first && l.Day <= today)
            .OrderByDescending(l => l.Day)
            .ThenBy(l => l.SystemId)
            .ThenBy(l => l.TypeId)
            .ThenBy(l => l.CorporationId)
            .ToArray()
            .Select(l => l.Snapshot())
            .ToArray();
    }

    internal static void Record(WorldDbContext world, Pilot pilot, WorldAsteroid resource, long now, MiningLedgerQuantity mined)
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
            world.Set<SavedPersonalMiningLog>().Where(l => l.PilotId == pilot.Id && l.Day < first).ExecuteDelete();
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
        if (
            MoonMiningRules.IsMoonRock(resource.Id)
            && world.Set<SavedMoonExtraction>().Find(MoonMiningRules.ExtractionId(resource.Id)) is { } extraction
        )
        {
            long structure = extraction.StructureId;
            long first = MiningLedgerRules.EarliestDay(now);
            world.Set<SavedMoonMiningLog>().Where(l => l.StructureId == structure && l.Day < first).ExecuteDelete();
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMoonMiningLog(StructureId,PilotId,Day,CorporationId,SystemId,TypeId,Quantity,Critical,Wasted)
                VALUES ({structure},{pilot.Id},{day},{pilot.CorporationId},{resource.SystemId},{resource.TypeId},
                    {quantity.Collected},{quantity.Critical},{quantity.Wasted})
                ON CONFLICT(StructureId,Day,PilotId,CorporationId,TypeId) DO UPDATE SET
                    Quantity=Quantity+excluded.Quantity,Critical=Critical+excluded.Critical,Wasted=Wasted+excluded.Wasted
                """
            );
        }
    }
}
