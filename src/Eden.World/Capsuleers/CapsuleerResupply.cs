using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable]
internal sealed class CapsuleerResupply(PilotMarketStore market)
{
    internal static long Limit(int type) =>
        MarketSnapshotCatalog.Quotes.GetValueOrDefault(type)?.SellCents is { } price
            ? checked(price * RattingRules.ReferencePriceMultiplier)
            : 0;

    internal static SupplyRequirement[] Requirements(
        WorldDatabase database,
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        SupplyRequirement[] previous
    )
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        var fit = FittingCalculation.Read(world, catalog, pilot);
        var inventory = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == ship.Id).ToArray();
        var correction = world.Set<SavedTrafficCorrection>().Find(pilotId);
        var identity = world.Set<SavedTrafficIdentity>().Find(pilotId);
        long kill = correction?.TemplateKillId ?? identity?.KillId ?? 0;
        var source = database.Traffic.Profiles.FirstOrDefault(p => p.KillId == kill);
        Dictionary<int, int> targets = [];
        foreach (var module in inventory.Where(i => i.Online && InventoryDefinitions.IsFittedSlot(i.FlagId)))
        {
            var charge = world.Set<OwnedItem>().SingleOrDefault(i => i.LocationId == module.Id);
            int type =
                charge?.TypeId
                ?? source
                    ?.Equipment.FirstOrDefault(e =>
                        e.Flag == module.FlagId && catalog.RequireItem(e.TypeId).CategoryId == (int)InventoryCategory.Charge
                    )
                    ?.TypeId
                ?? 0;
            if (type == 0)
            {
                continue;
            }
            var effect = FittingCatalog.DefaultEffect(catalog, module.TypeId);
            if (
                effect
                is not (
                    DogmaEffect.LaserFired
                    or DogmaEffect.ProjectileFired
                    or DogmaEffect.UseMissiles
                    or DogmaEffect.CapacitorBoosting
                    or DogmaEffect.AncillaryShieldBoosting
                    or DogmaEffect.AncillaryArmorRepair
                )
            )
            {
                continue;
            }
            var ammo = catalog.FittingAttributes(type);
            if (
                Eden.Gameplay.Ammunition.AmmunitionDefinitions.IsCrystalGroup(catalog.RequireItem(type).GroupId)
                && ammo.GetValueOrDefault(DogmaAttributeId.CrystalsGetDamaged) == 0
            )
            {
                continue;
            }
            double volume = PilotInventoryStore.Volume(catalog, type);
            int magazine = volume > 0 ? (int)Math.Floor((catalog.RequireItem(module.TypeId).Capacity ?? 0) / volume) : 0;
            targets[type] = checked(
                targets.GetValueOrDefault(type)
                + magazine * CapsuleerSupplyStore.SpareMagazines
                + Math.Max(0, magazine - (charge?.Quantity ?? 0))
            );
        }
        List<SupplyRequirement> result = [];
        foreach (var pair in targets.OrderBy(p => p.Key))
        {
            int owned = checked(
                (int)
                    Math.Min(
                        int.MaxValue,
                        inventory.Where(i => i.FlagId == (int)InventoryFlag.Cargo && i.TypeId == pair.Key).Sum(i => i.Quantity)
                    )
            );
            if (owned < pair.Value)
            {
                result.Add(new(pair.Key, pair.Value - owned, false, PilotInventoryStore.Volume(catalog, pair.Key)));
            }
        }
        if (DroneRules.SupportsHull(ship.TypeId))
        {
            double bay = fit.Graph.Read(ship.Id, DogmaAttributeId.DroneCapacity);
            double bandwidth = fit.Graph.Read(ship.Id, DogmaAttributeId.DroneBandwidth);
            int controls = DroneCatalog.ControlLimit(fit);
            int preferred =
                inventory.FirstOrDefault(i => i.FlagId == (int)InventoryFlag.DroneBay)?.TypeId
                ?? previous.FirstOrDefault(r => r.Drone)?.TypeId
                ?? 0;
            bool Usable(int type)
            {
                try
                {
                    PilotSkillReader.CheckRequirements(world, catalog, pilotId, type);
                    return true;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            }
            var candidates = CapsuleerSupplyStore
                .AvailableDrones(catalog)
                .Where(d => Usable(d.TypeId))
                .Select(d => new
                {
                    Profile = d,
                    Count = (int)Math.Min(controls, Math.Min(Math.Floor(bay / d.Volume), Math.Floor(bandwidth / d.Bandwidth))),
                })
                .Where(d => d.Count > 0)
                .OrderByDescending(d => d.Profile.TypeId == preferred)
                .ThenByDescending(d => d.Count)
                .ThenByDescending(d => d.Profile.Bandwidth)
                .ThenBy(d => d.Profile.TypeId)
                .ToArray();
            if (candidates.FirstOrDefault() is { } desired)
            {
                long owned = inventory.Where(i => i.FlagId == (int)InventoryFlag.DroneBay).Sum(i => i.Quantity);
                if (owned < desired.Count)
                {
                    result.Add(new(desired.Profile.TypeId, checked(desired.Count - (int)owned), true, desired.Profile.Volume));
                }
            }
        }
        return result.ToArray();
    }

    internal ResupplyProgress Dock(
        WorldDatabase database,
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        ResupplyProgress? saved,
        long now
    )
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        var progress =
            saved?.StationId == ship.StationId
                ? saved
                : new ResupplyProgress(
                    ship.StationId,
                    (world.Set<PilotWallet>().Find(pilotId)?.Cents ?? 0) / RattingRules.BudgetDivisor,
                    0
                );
        if (progress.Checked)
        {
            return progress;
        }
        var needs = Requirements(database, world, catalog, pilotId, progress.Requirements);
        foreach (var need in needs.OrderBy(r => r.Drone).ThenBy(r => r.TypeId))
        {
            int remaining = need.Quantity;
            var stock = world
                .Set<OwnedItem>()
                .Where(i =>
                    i.OwnerId == pilotId
                    && i.LocationId == ship.StationId
                    && i.FlagId == (int)InventoryFlag.Hangar
                    && i.TypeId == need.TypeId
                )
                .OrderBy(i => i.Id)
                .ToArray();
            foreach (var stack in stock)
            {
                int quantity = Capacity(world, catalog, pilotId, ship.Id, need, Math.Min(remaining, checked((int)stack.Quantity)));
                if (quantity > 0)
                {
                    Load(world, catalog, pilotId, ship.Id, ship.StationId, stack.Id, quantity, need.Drone, now);
                    remaining -= quantity;
                }
            }
            long limit = Limit(need.TypeId);
            if (remaining <= 0 || limit <= 0)
            {
                continue;
            }
            var orders = market
                .SupplyOrders(world, catalog, ship.StationId, [need.TypeId])
                .Where(o => o.OwnerId != pilotId && o.PriceCents <= limit)
                .ToArray();
            foreach (var order in orders)
            {
                long funds = Math.Min(progress.BudgetCents - progress.SpentCents, world.Set<PilotWallet>().Find(pilotId)!.Cents);
                int quantity = Capacity(
                    world,
                    catalog,
                    pilotId,
                    ship.Id,
                    need,
                    (int)Math.Min(remaining, Math.Min(order.Remaining, funds / order.PriceCents))
                );
                if (quantity <= 0)
                {
                    continue;
                }
                var purchase = market.ExecuteBuy(
                    world,
                    catalog,
                    pilotId,
                    ship.StationId,
                    [new(need.TypeId, order.PriceCents, quantity)],
                    0
                );
                long spent = purchase.Trades.Sum(t => checked(t.Quantity * t.PriceCents));
                int bought = purchase.Trades.Sum(t => t.Quantity);
                progress = progress with { SpentCents = checked(progress.SpentCents + spent) };
                remaining -= bought;
                var delivered = world
                    .Set<OwnedItem>()
                    .Where(i =>
                        i.OwnerId == pilotId
                        && i.LocationId == ship.StationId
                        && i.FlagId == (int)InventoryFlag.Hangar
                        && i.TypeId == need.TypeId
                    )
                    .OrderBy(i => i.Id)
                    .ToArray();
                foreach (var stack in delivered)
                {
                    int amount = Math.Min(bought, checked((int)stack.Quantity));
                    if (amount > 0)
                    {
                        Load(world, catalog, pilotId, ship.Id, ship.StationId, stack.Id, amount, need.Drone, now);
                        bought -= amount;
                    }
                }
            }
        }
        Reload(database, world, catalog, pilotId, now);
        return progress with { Checked = true, Requirements = Requirements(database, world, catalog, pilotId, needs) };
    }

    private static void Reload(WorldDatabase database, WorldDbContext world, CatalogDbContext catalog, long pilotId, long now)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        long ship = pilot.ActiveShipId;
        long kill =
            world.Set<SavedTrafficCorrection>().Find(pilotId)?.TemplateKillId
            ?? world.Set<SavedTrafficIdentity>().Find(pilotId)?.KillId
            ?? 0;
        var profile = database.Traffic.Profiles.FirstOrDefault(p => p.KillId == kill);
        var inventory = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == ship).ToArray();
        foreach (var module in inventory.Where(i => i.Online && InventoryDefinitions.IsFittedSlot(i.FlagId)))
        {
            var loaded = world.Set<OwnedItem>().SingleOrDefault(i => i.LocationId == module.Id);
            int type =
                loaded?.TypeId
                ?? profile
                    ?.Equipment.FirstOrDefault(e =>
                        e.Flag == module.FlagId && catalog.RequireItem(e.TypeId).CategoryId == (int)InventoryCategory.Charge
                    )
                    ?.TypeId
                ?? 0;
            var effect = FittingCatalog.DefaultEffect(catalog, module.TypeId);
            if (
                type == 0
                || effect
                    is not (
                        DogmaEffect.LaserFired
                        or DogmaEffect.ProjectileFired
                        or DogmaEffect.UseMissiles
                        or DogmaEffect.CapacitorBoosting
                        or DogmaEffect.AncillaryShieldBoosting
                        or DogmaEffect.AncillaryArmorRepair
                    )
                || world.Set<ModuleCooldown>().Find(module.Id)?.ReadyAt > now
            )
            {
                continue;
            }
            double volume = PilotInventoryStore.Volume(catalog, type);
            int maximum = volume > 0 ? (int)Math.Floor((catalog.RequireItem(module.TypeId).Capacity ?? 0) / volume) : 0;
            if ((loaded?.Quantity ?? 0) >= maximum)
            {
                continue;
            }
            var sources = world
                .Set<OwnedItem>()
                .Where(i => i.OwnerId == pilotId && i.LocationId == ship && i.FlagId == (int)InventoryFlag.Cargo && i.TypeId == type)
                .Select(i => i.Id)
                .ToArray();
            if (sources.Length > 0)
            {
                PilotAmmunitionStore.Load(world, catalog, pilotId, ship, [module.Id], sources, ship, now);
            }
        }
    }

    internal CapsuleerTrafficRoute? Shop(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        int station,
        HullClass hull,
        ResupplyProgress progress,
        IReadOnlySet<int> avoided
    )
    {
        long funds = Math.Min(progress.BudgetCents - progress.SpentCents, world.Set<PilotWallet>().Find(pilotId)!.Cents);
        var types = progress.Requirements.Where(r => Limit(r.TypeId) > 0).Select(r => r.TypeId).ToArray();
        if (types.Length == 0 || funds <= 0)
        {
            return null;
        }
        int region = catalog.RequireStation(station).RegionId;
        // Search actual existing orders; shopping never manufactures a remote stock history.
        var stations = world
            .Set<NpcMarketOrder>()
            .Where(o => o.RegionId == region && !o.Bid && o.Remaining > 0 && types.Contains(o.TypeId))
            .Select(o => o.StationId)
            .ToArray()
            .Concat(
                world
                    .Set<SavedPersonalOrder>()
                    .Where(o =>
                        o.RegionId == region
                        && !o.Bid
                        && o.Remaining > 0
                        && o.Status == PersonalOrderStatus.Open
                        && types.Contains(o.TypeId)
                    )
                    .Select(o => o.StationId)
                    .ToArray()
            )
            .Distinct()
            .Where(id => id != station);
        List<ShoppingCandidate> candidates = [];
        foreach (int target in stations)
        {
            var route = CapsuleerTrafficRoutes.ToStation(catalog, station, target, hull, avoided, RattingRules.MaximumShoppingJumps);
            if (route is null || route.Systems.Length - 1 > RattingRules.MaximumShoppingJumps)
            {
                continue;
            }
            var orders = market
                .SupplyOrders(world, catalog, target, types)
                .Where(o => o.OwnerId != pilotId && o.PriceCents <= Limit(o.TypeId) && o.PriceCents <= funds)
                .ToArray();
            int coverage = progress.Requirements.Count(r => orders.Any(o => o.TypeId == r.TypeId));
            if (coverage > 0)
            {
                candidates.Add(new(route, coverage, orders.Min(o => o.PriceCents)));
            }
        }
        return candidates
            .OrderBy(c => c.Route.Systems.Length)
            .ThenByDescending(c => c.Coverage)
            .ThenBy(c => c.Price)
            .ThenBy(c => c.Route.DestinationStationId)
            .FirstOrDefault()
            ?.Route;
    }

    private sealed record ShoppingCandidate(CapsuleerTrafficRoute Route, int Coverage, long Price);

    private static int Capacity(WorldDbContext world, CatalogDbContext catalog, long pilot, long ship, SupplyRequirement need, int desired)
    {
        var flag = need.Drone ? InventoryFlag.DroneBay : InventoryFlag.Cargo;
        var attribute = need.Drone ? DogmaAttributeId.DroneCapacity : DogmaAttributeId.CargoCapacity;
        double free =
            PilotFittingStore.ShipAttributes(world, catalog, pilot).GetValueOrDefault(attribute)
            - InventoryPersistence.UsedVolume(world, catalog, pilot, ship, (int)flag);
        return need.Volume > 0 ? (int)Math.Max(0, Math.Min(desired, Math.Floor(free / need.Volume))) : 0;
    }

    private static void Load(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilot,
        long ship,
        int station,
        long item,
        int quantity,
        bool drone,
        long now
    ) =>
        PilotInventoryStore.Move(
            world,
            catalog,
            pilot,
            ship,
            new([item], station, (int)(drone ? InventoryFlag.DroneBay : InventoryFlag.Cargo), quantity),
            now
        );
}
