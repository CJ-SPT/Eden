using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Economy;
using Eden.Data.World.Insurance;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Content;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Economy;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Insurance;

internal sealed partial class PilotInsuranceStore
{
    private static long Value(WorldAction world, CatalogDbContext catalog, int typeId)
    {
        var type = catalog.Definitions.Items.GetValueOrDefault(typeId);
        if (type?.CategoryId != (int)InventoryCategory.Ship || type.GroupId == FleetDefinitions.CapsuleGroup)
        {
            return 0;
        }
        if (world.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts)
        {
            if (type.GroupId == FleetDefinitions.CorvetteGroup)
            {
                // A freely recovered hull must not mint ISK through repeated losses.
                return 0;
            }
            if (BuildContentPolicy.Current.Insurance.MaterialValuedHullTypes.Contains(typeId))
            {
                var blueprints = catalog.Definitions.BlueprintsForProduct(typeId);
                var recipe = blueprints.Length == 1 ? catalog.Definitions.ManufacturingRecipe(blueprints[0]) : null;
                if (recipe?.Materials is not { Length: > 0 })
                {
                    return 0;
                }
                var materials = recipe.Materials.Value.ToDictionary(
                    m => checked((int)m.TypeID!.Value),
                    m => checked((int)m.Quantity!.Value)
                );
                int[] ids = materials.Keys.ToArray();
                var prices = world
                    .Set<SavedAdjustedPrice>()
                    .Where(row => Enumerable.Contains(ids, row.TypeId))
                    .ToDictionary(row => row.TypeId, row => row.PriceIsk);
                return checked((long)Math.Round(EconomicRules.Value(materials, prices) * 100, 0, MidpointRounding.ToEven));
            }
        }
        return InsuranceRules.CatalogValue(catalog.Definitions.Find(CatalogData.Client.Types, (long)typeId)?.BasePrice);
    }

    internal static InsurancePayout? SettleLoss(WorldAction world, CatalogDbContext catalog, Ship ship, long now)
    {
        if (world.Set<SavedInsuranceLedger>().Any(entry => entry.ShipId == ship.Id && entry.IsPayout))
        {
            return null;
        }
        var policy = world
            .Set<SavedInsurancePolicy>()
            .SingleOrDefault(p => p.ShipId == ship.Id && p.Status == InsurancePolicyStatus.Active);
        bool covered =
            policy is not null
            && policy.PilotId == ship.OwnerId
            && policy.TypeId == ship.TypeId
            && policy.StartTime <= now
            && policy.EndTime > now;
        long value = covered ? policy!.ValueCents : Value(world, catalog, ship.TypeId);
        if (policy is not null)
        {
            policy.Status = covered ? InsurancePolicyStatus.Paid : InsurancePolicyStatus.Expired;
        }
        if (value == 0)
        {
            return null;
        }
        long amount = InsuranceRules.Payout(value, covered ? policy!.Level : InsuranceLevel.Uninsured);
        var wallet = world.Set<PilotWallet>().Find(ship.OwnerId) ?? world.Add(new PilotWallet { PilotId = ship.OwnerId }).Entity;
        wallet.Cents = checked(wallet.Cents + amount);
        world.Add(
            new SavedInsuranceLedger
            {
                PilotId = ship.OwnerId,
                ShipId = ship.Id,
                TypeId = ship.TypeId,
                PolicyId = covered ? policy!.Id : null,
                IsPayout = true,
                AmountCents = amount,
                BalanceCents = wallet.Cents,
                Time = now,
            }
        );
        return new(ship.Id, amount, wallet.Cents);
    }

    internal static void VoidRemovedHull(WorldAction world, long shipId)
    {
        var policy = world.Set<SavedInsurancePolicy>().SingleOrDefault(p => p.ShipId == shipId && p.Status == InsurancePolicyStatus.Active);
        if (policy is not null)
        {
            policy.Status = InsurancePolicyStatus.HullRemoved;
        }
    }
}
