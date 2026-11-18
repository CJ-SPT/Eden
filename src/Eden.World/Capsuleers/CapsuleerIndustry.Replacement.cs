using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerReplacementRules;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    internal bool ReplaceMerlin(EconomyTransaction world, CatalogDbContext catalog, DockedPilot docked, long now, out bool marketChanged)
    {
        marketChanged = false;
        long pilotId = docked.PilotId;
        int station = docked.StationId;
        if (
            docked.Position is not null
            || docked.InStructure
            || docked.ShipGroupId != FleetDefinitions.CapsuleGroup
            || !StationCapabilities.Has(catalog, docked.OperationId, StationService.Market)
            || Employer(world, pilotId, docked.CorpId) is not { } corporation
            || !world
                .Filter<ShipLoss>(loss => loss.CapsuleId == docked.ShipId)
                .Any(loss => loss.PilotId == pilotId && loss.TypeId == Rules.HullType)
        )
        {
            return false;
        }
        var pilot = world.Pilots.Find(pilotId)!;
        if (pilot.ActiveShipId != docked.ShipId || pilot.ActiveShip.Position is not null || pilot.ActiveShip.DockedLocationId != station)
        {
            return false;
        }

        var bids = world
            .Filter<SavedPersonalOrder>(order => order.PilotId == pilotId)
            .Where(order =>
                order.CorporationId == corporation.Id
                && order.StationId == station
                && order.Bid
                && order.Status == PersonalOrderStatus.Open
                && order.Expires > now
            )
            .AsEnumerable()
            .Where(order => Rules.Parts.Any(part => part.TypeId == order.TypeId))
            .ToLookup(order => order.TypeId);
        var hangar = Hangar(world, pilotId, station).ToLookup(item => item.TypeId);
        var missing = Rules
            .Parts.Select(part => new ReplacementPart(
                part.TypeId,
                (int)Math.Max(0, part.Quantity - hangar[part.TypeId].Where(item => !item.Singleton).Sum(item => (long)item.Quantity))
            ))
            .Where(part => part.Quantity > 0)
            .ToArray();
        if (missing.Length > 0)
        {
            foreach (var part in missing.Where(part => !bids.Contains(part.TypeId)))
            {
                PilotSkillReader.CheckRequirements(world, catalog, pilotId, part.TypeId);
            }
            var depot = DepotStacks(world, corporation.Id, station).OrderBy(item => item.Id).ToLookup(item => item.TypeId);
            foreach (var part in missing.Where(part => !bids.Contains(part.TypeId)))
            {
                marketChanged |= ProcureReplacementPart(world, catalog, corporation, pilotId, station, part, depot[part.TypeId]);
            }
            // Purchases and escrow commit on the docked job. The next job works from owned hangar stock.
            return false;
        }

        foreach (var bid in bids.SelectMany(group => group))
        {
            market.CancelCorporationBid(world, catalog, bid);
            marketChanged = true;
        }
        var package = hangar[Rules.HullType].First(item => !item.Singleton);
        var assembly = PilotFleetStore.Assemble(world, catalog, pilotId, [package.Id], name: null);
        long shipId = assembly.ShipIds.Single();
        var sources = hangar
            .Where(group => group.Key != Rules.HullType)
            .ToDictionary(
                group => group.Key,
                IReadOnlyList<long> (group) => group.Where(item => !item.Singleton).Select(item => item.Id).ToArray()
            );
        var request = new ApplyFittingRequest(shipId, Rules.HullType, station, sources, Rules.Slots, Rules.Supplies, FitRigs: false);
        if (fitting.Apply(world, catalog, pilot, world.Ships.Find(shipId)!, request).Missing.Count > 0)
        {
            throw new InvalidDataException("Merlin replacement inventory changed before fitting.");
        }
        PilotFleetStore.Board(world, catalog, pilotId, shipId, docked.ShipId, now);
        var fitted = world.Filter<OwnedItem>(item => item.LocationId == shipId).ToArray();
        PilotAmmunitionStore.Load(
            world,
            catalog,
            pilotId,
            shipId,
            Rules.Slots.Select(slot => fitted.Single(item => item.FlagId == slot.FlagId).Id).ToArray(),
            fitted
                .Where(item => item.FlagId == (int)InventoryFlag.Cargo && item.TypeId == Rules.Supplies[0].TypeId)
                .Select(item => item.Id)
                .ToArray(),
            shipId,
            now
        );
        return true;
    }

    private bool ProcureReplacementPart(
        EconomyTransaction world,
        CatalogDbContext catalog,
        SavedCorporation corporation,
        long pilot,
        int station,
        ReplacementPart part,
        IEnumerable<OwnedItem> depot
    )
    {
        int remaining = part.Quantity;
        foreach (var stock in depot)
        {
            int quantity = Math.Min(remaining, stock.Quantity);
            InventoryPersistence.MergeOutput(world, catalog, pilot, station, (int)InventoryFlag.Hangar, part.TypeId, quantity);
            stock.Quantity -= quantity;
            remaining -= quantity;
            if (stock.Quantity == 0)
            {
                world.Remove(stock);
            }
            if (remaining == 0)
            {
                return false;
            }
        }
        long price = Rules.LimitPrice(ReferencePrice(catalog, part.TypeId));
        if (price <= 0 || !PilotMarketStore.HasSupplyListingSlot(world, catalog, pilot))
        {
            return false;
        }
        long cost = checked(price * remaining + PilotMarketStore.SupplyBrokerFee(world, catalog, pilot, station, price, remaining));
        if (corporation.BalanceCents < cost)
        {
            return false;
        }
        market.BuyCorporationStock(world, catalog, pilot, station, corporation, [new(part.TypeId, price, remaining)], Rules.OrderDays);
        return true;
    }
}
