using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Combat;

[Injectable]
internal sealed class PilotSecurityOffice(WorldDatabase database, TimeProvider clock, SecurityStatusSignal signal) : IPilotSecurityOffice
{
    public IReadOnlyList<InventoryChange> Exchange(long pilotId, SecurityTagRequest request)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("The Security Office requires the resident world.");
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var world = live.Economy.Begin(new(pilots: [pilotId]));
        if (
            world.Pilots.Find(pilotId)?.ActiveShip is not { Position: null, StructureId: null } ship
            || catalog.Definitions.FindStation(ship.StationId) is not { } station
            || !SecurityOfficeRules.Available(station.OwnerId, catalog.Definitions.SystemSecurity.GetValueOrDefault(station.SolarSystemId))
        )
        {
            throw new InvalidDataException("Dock at a low-security CONCORD or DED Security Office to exchange tags.");
        }

        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        PilotSecurityStatusStore.Settle(world, pilotId, now);
        var status = world.Set<SavedSecurityStatus>().Find(pilotId);
        double current = status?.Value ?? 0;
        var plan = SecurityOfficeRules.Plan(current, request);
        long cost = 0;
        foreach (var tag in plan.Tags)
        {
            double fee = catalog.RawFittingAttributes(tag.TypeId).GetValueOrDefault(DogmaAttributeId.SecurityProcessingFee);
            if (!double.IsFinite(fee) || fee <= 0 || fee >= (double)long.MaxValue / MarketDefinitions.CentsPerIsk)
            {
                throw new InvalidDataException("This security tag has no valid processing fee.");
            }
            long cents = checked((long)Math.Round(fee * MarketDefinitions.CentsPerIsk, MidpointRounding.AwayFromZero));
            cost = checked(cost + cents * tag.Quantity);
        }

        var wallet = world.Set<PilotWallet>().Find(pilotId);
        if (wallet is null || wallet.Cents < cost)
        {
            throw new InvalidDataException("You do not have enough ISK to pay the security tag processing fee.");
        }
        List<InventoryChange> changes = [];
        foreach (var tag in plan.Tags)
        {
            int remaining = tag.Quantity;
            var stacks = world
                .Filter<OwnedItem>(item =>
                    item.OwnerId == pilotId
                    && item.LocationId == station.Id
                    && item.FlagId == (int)InventoryFlag.Hangar
                    && item.TypeId == tag.TypeId
                    && !item.Singleton
                    && item.Quantity > 0
                )
                .OrderBy(item => item.Id)
                .ToArray();
            foreach (var stack in stacks)
            {
                var previous = PilotInventoryStore.Read(catalog, stack);
                int spent = Math.Min(remaining, stack.Quantity);
                stack.Quantity -= spent;
                remaining -= spent;
                changes.Add(new(previous with { Quantity = stack.Quantity }, previous));
                if (stack.Quantity == 0)
                {
                    world.Remove(stack);
                }
                if (remaining == 0)
                {
                    break;
                }
            }
            if (remaining != 0)
            {
                throw new InvalidDataException("Place the required security tags in your personal Item Hangar at this station.");
            }
        }

        status!.Value = plan.Status;
        wallet.Cents -= cost;
        world.Add(
            new SavedServicePayment
            {
                PilotId = pilotId,
                CounterpartyId = ConcordRules.Corporation,
                ContextId = station.Id,
                Kind = WalletEntryType.SecurityTagProcessingFee,
                AmountCents = -cost,
                BalanceCents = wallet.Cents,
                Time = now,
            }
        );
        world.Add(
            new SavedSecurityTransaction
            {
                PilotId = pilotId,
                Time = now,
                TypeId = plan.Tags[0].TypeId,
                SystemId = station.SolarSystemId,
                CorporationId = station.OwnerId,
                Modification = (plan.Status - current) / (SecurityStatusRules.Maximum - current),
                NewValue = plan.Status,
            }
        );
        world.AfterCommit(signal.Publish);
        world.Commit();
        return changes;
    }
}
