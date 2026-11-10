namespace Eden.Gameplay.Structures;

public sealed record StructureFuelCycle(long ItemId, int Slot, int GroupId, int Quantity, long NextFuelAt);

public sealed record StructureFuelReserve(int GroupId, long Quantity);

public sealed record StructureFuelCharge(int GroupId, long Quantity);

public sealed record StructureFuelDeadline(long ItemId, int Slot, long? NextFuelAt, long? OfflineAt);

public sealed record StructureFuelPlan(IReadOnlyList<StructureFuelCharge> Charges, IReadOnlyList<StructureFuelDeadline> Deadlines);

public static class StructureFuelRules
{
    public static StructureFuelPlan Advance(
        IReadOnlyList<StructureFuelCycle> services,
        IReadOnlyList<StructureFuelReserve> reserves,
        long until
    )
    {
        var remaining = reserves.ToDictionary(r => r.GroupId, r => r.Quantity);
        Dictionary<int, long> charges = [];
        Dictionary<long, StructureFuelDeadline> deadlines = [];
        PriorityQueue<StructureFuelCycle, (long At, int Slot, long Item)> pending = new();
        foreach (var service in services)
        {
            if (service.Quantity < 0)
            {
                throw new InvalidDataException("Service fuel consumption cannot be negative.");
            }
            if (service.Quantity == 0)
            {
                long next = service.NextFuelAt;
                if (next <= until)
                {
                    next = checked(next + ((until - next) / StructureRules.FuelInterval + 1) * StructureRules.FuelInterval);
                }
                deadlines.Add(service.ItemId, new(service.ItemId, service.Slot, next, null));
            }
            else
            {
                pending.Enqueue(service, (service.NextFuelAt, service.Slot, service.ItemId));
            }
        }
        // Every paid iteration consumes real finite stock. Free services skip directly to their next future deadline.
        // Deadline/slot order preserves competition between services across late callbacks and restarts.
        while (pending.TryDequeue(out var service, out _))
        {
            if (service.NextFuelAt > until)
            {
                deadlines.Add(service.ItemId, new(service.ItemId, service.Slot, service.NextFuelAt, null));
            }
            else if (remaining.GetValueOrDefault(service.GroupId) < service.Quantity)
            {
                deadlines.Add(service.ItemId, new(service.ItemId, service.Slot, null, service.NextFuelAt));
            }
            else
            {
                remaining[service.GroupId] -= service.Quantity;
                charges[service.GroupId] = charges.GetValueOrDefault(service.GroupId) + service.Quantity;
                service = service with { NextFuelAt = checked(service.NextFuelAt + StructureRules.FuelInterval) };
                pending.Enqueue(service, (service.NextFuelAt, service.Slot, service.ItemId));
            }
        }
        return new(
            charges.OrderBy(p => p.Key).Select(p => new StructureFuelCharge(p.Key, p.Value)).ToArray(),
            deadlines.Values.OrderBy(d => d.Slot).ToArray()
        );
    }
}
