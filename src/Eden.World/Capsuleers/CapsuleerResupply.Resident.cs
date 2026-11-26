using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerResupply
{
    private sealed record SupplyMagazine(long Module, int Type, int Capacity, int Loaded);

    private static List<SupplyMagazine> ReadMagazines(
        WorldDatabase database,
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        OwnedItem[] inventory
    )
    {
        long pilotId = pilot.Id;
        var correction = world.Set<SavedTrafficCorrection>().Find(pilotId);
        var identity = world.Set<SavedTrafficIdentity>().Find(pilotId);
        long kill = correction?.TemplateKillId ?? identity?.KillId ?? 0;
        var source = database.Traffic.Find(kill);
        var modules = inventory.Where(i => i.Online && InventoryDefinitions.IsFittedSlot(i.FlagId)).ToArray();
        var moduleIds = modules.Select(i => i.Id).ToArray();
        // Keep Single's corruption check while loading every magazine in one indexed query.
        var charges =
            moduleIds.Length == 0 ? [] : world.Set<OwnedItem>().Where(i => Enumerable.Contains(moduleIds, i.LocationId)).ToArray();
        var magazines = charges.ToLookup(i => i.LocationId);
        List<SupplyMagazine> result = [];
        foreach (var module in modules)
        {
            var charge = magazines[module.Id].SingleOrDefault();
            int type =
                charge?.TypeId
                ?? source
                    ?.Equipment.FirstOrDefault(e =>
                        e.Flag == module.FlagId && catalog.RequireItem(e.TypeId).CategoryId == (int)InventoryCategory.Charge
                    )
                    ?.TypeId
                ?? (
                    pilot.CorporationId >= CorporationRules.FirstCorporationId
                    && CapsuleerIndustry.CanUseIron(catalog, module.TypeId)
                    && CapsuleerIndustry.Employer(world, pilotId, pilot.CorporationId) is not null
                        ? CapsuleerAmmunitionSupplyRules.ChargeType
                        : 0
                );
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
            double volume = PilotInventoryStore.Volume(catalog, type);
            int magazine = volume > 0 ? (int)Math.Floor((catalog.RequireItem(module.TypeId).Capacity ?? 0) / volume) : 0;
            result.Add(new(module.Id, type, magazine, charge?.Quantity ?? 0));
        }
        return result;
    }

    internal static SupplyRequirement[] Requirements(
        WorldDatabase database,
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        SupplyRequirement[] previous
    )
    {
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        var inventory = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == ship.Id).ToArray();
        var magazines = ReadMagazines(database, world, catalog, pilot, inventory);
        FittingCalculation? fit = null;
        return Requirements(world, catalog, pilot, inventory, magazines, previous, ref fit);
    }

    private static SupplyRequirement[] Requirements(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        OwnedItem[] inventory,
        IReadOnlyList<SupplyMagazine> magazines,
        SupplyRequirement[] previous,
        ref FittingCalculation? fit
    )
    {
        long pilotId = pilot.Id;
        var ship = pilot.ActiveShip;
        Dictionary<int, int> targets = [];
        foreach (var magazine in magazines)
        {
            int type = magazine.Type;
            var ammo = catalog.FittingAttributes(type);
            if (
                Eden.Gameplay.Ammunition.AmmunitionDefinitions.IsCrystalGroup(catalog.RequireItem(type).GroupId)
                && ammo.GetValueOrDefault(DogmaAttributeId.CrystalsGetDamaged) == 0
            )
            {
                continue;
            }
            targets[type] = checked(
                targets.GetValueOrDefault(type)
                + magazine.Capacity * CapsuleerSupplyStore.SpareMagazines
                + Math.Max(0, magazine.Capacity - magazine.Loaded)
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
            var snapshot = fit is null ? world.CachedFittingSnapshot(pilotId, ship.Id, world.FitRevision(pilotId)) : null;
            var droneFit = snapshot is null ? fit ??= FittingCalculation.Read(world, catalog, pilot) : null;
            int controls = snapshot is null ? DroneCatalog.ControlLimit(droneFit!) : checked((int)snapshot.DroneControlLimit);
            long owned = inventory.Where(i => i.FlagId == (int)InventoryFlag.DroneBay).Sum(i => i.Quantity);
            if (owned >= controls)
            {
                // Every candidate is bounded by the control limit. The existing flight
                // already covers its possible count, so no replacement purchase is needed.
                return result.ToArray();
            }
            int preferred =
                inventory.FirstOrDefault(i => i.FlagId == (int)InventoryFlag.DroneBay)?.TypeId
                ?? previous.FirstOrDefault(r => r.Drone)?.TypeId
                ?? 0;
            double bay = snapshot?.DroneCapacity ?? droneFit!.Graph.Read(ship.Id, DogmaAttributeId.DroneCapacity);
            double bandwidth = snapshot?.DroneBandwidth ?? droneFit!.Graph.Read(ship.Id, DogmaAttributeId.DroneBandwidth);
            var candidates = CapsuleerSupplyStore
                .AvailableDrones(catalog)
                .Select(d => new DroneCandidate(
                    d,
                    (int)Math.Min(controls, Math.Min(Math.Floor(bay / d.Volume), Math.Floor(bandwidth / d.Bandwidth)))
                ))
                .Where(d => d.Count > 0)
                .ToArray();
            var usable = PilotSkillReader.UsableTypes(
                world,
                catalog,
                pilotId,
                candidates.Select(candidate => candidate.Profile.TypeId).ToArray()
            );
            var desired = candidates
                .Where(candidate => Enumerable.Contains(usable, candidate.Profile.TypeId))
                .OrderByDescending(d => d.Profile.TypeId == preferred)
                .ThenByDescending(d => d.Count)
                .ThenByDescending(d => d.Profile.Bandwidth)
                .ThenBy(d => d.Profile.TypeId)
                .FirstOrDefault();
            if (desired is not null)
            {
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
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        ResupplyProgress? saved,
        long now
    ) => Dock(database, world, catalog, pilotId, saved, now, out _);

    internal ResupplyProgress Dock(
        WorldDatabase database,
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        ResupplyProgress? saved,
        long now,
        out FittingCalculation? currentFit,
        IReadOnlyList<ReplacementPart>? fleetReserve = null
    )
    {
        using var phase = SessionPerformance.Phase("traffic.resupply");
        currentFit = null;
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
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
        SupplyRequirement[] needs;
        List<SupplyMagazine> magazines;
        FittingCalculation? fit = null;
        OwnedItem[] ReadInventory() => world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == ship.Id).ToArray();
        using (SessionPerformance.Phase("traffic.resupplyRequirements"))
        {
            var inventory = ReadInventory();
            magazines = ReadMagazines(database, world, catalog, pilot, inventory);
            needs = Requirements(world, catalog, pilot, inventory, magazines, progress.Requirements, ref fit);
        }
        var neededTypes = needs.Select(need => need.TypeId).Distinct().ToArray();
        var hangarItems =
            neededTypes.Length == 0
                ? []
                : world
                    .Set<OwnedItem>()
                    .Where(i =>
                        i.OwnerId == pilotId
                        && i.LocationId == ship.StationId
                        && i.FlagId == (int)InventoryFlag.Hangar
                        && Enumerable.Contains(neededTypes, i.TypeId)
                    )
                    .OrderBy(i => i.Id)
                    .ToArray();
        var hangar = hangarItems.ToLookup(i => i.TypeId);
        var purchaseTypes = needs
            .Where(need => need.Quantity > hangar[need.TypeId].Sum(i => i.Quantity) && Limit(need.TypeId) > 0)
            .Select(need => need.TypeId)
            .Distinct()
            .ToArray();
        IReadOnlyList<MarketOrder> availableOrders;
        using (SessionPerformance.Phase("traffic.resupplyOffers"))
        {
            availableOrders = purchaseTypes.Length == 0 ? [] : market.SupplyOrders(world, catalog, ship.StationId, purchaseTypes);
        }
        var supply = availableOrders.ToLookup(order => order.TypeId);
        IReadOnlyDictionary<DogmaAttributeId, double>? attributes = null;
        // Stock purchases and transfers only touch the hangar and holds until Reload.
        IReadOnlyDictionary<DogmaAttributeId, double> CapacityAttributes() =>
            attributes ??= PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: fit);
        int Capacity(SupplyRequirement need, int desired)
        {
            if (desired <= 0)
            {
                return 0;
            }
            var flag = need.Drone ? InventoryFlag.DroneBay : InventoryFlag.Cargo;
            var attribute = need.Drone ? DogmaAttributeId.DroneCapacity : DogmaAttributeId.CargoCapacity;
            double free =
                CapacityAttributes().GetValueOrDefault(attribute)
                - InventoryPersistence.UsedVolume(world, catalog, pilotId, ship.Id, (int)flag);
            return need.Volume > 0 ? (int)Math.Max(0, Math.Min(desired, Math.Floor(free / need.Volume))) : 0;
        }
        bool inventoryChanged = false;
        foreach (var need in needs.OrderBy(r => r.Drone).ThenBy(r => r.TypeId))
        {
            int remaining = need.Quantity;
            var stock = hangar[need.TypeId];
            int availableFromStock = remaining;
            if (fleetReserve?.FirstOrDefault(part => part.TypeId == need.TypeId) is { } reserved)
            {
                long localQuantity =
                    stock.Sum(item => (long)item.Quantity)
                    + CapsuleerIndustry.DepotStockQuantity(world, pilot.CorporationId, ship.StationId, need.TypeId);
                availableFromStock = (int)Math.Clamp(localQuantity - reserved.Quantity, 0, remaining);
            }
            foreach (var stack in stock)
            {
                if (availableFromStock <= 0)
                {
                    break;
                }
                int quantity = Capacity(need, Math.Min(availableFromStock, checked((int)stack.Quantity)));
                if (quantity > 0)
                {
                    Load(world, catalog, pilotId, ship.Id, ship.StationId, stack.Id, quantity, need.Drone, now, CapacityAttributes());
                    inventoryChanged = true;
                    remaining -= quantity;
                    availableFromStock -= quantity;
                }
            }
            int capacity = Capacity(need, availableFromStock);
            if (
                CapsuleerIndustry.IssueReserve(world, catalog, pilotId, pilot.CorporationId, ship.StationId, need.TypeId, capacity) is
                { } reserve
            )
            {
                Load(
                    world,
                    catalog,
                    pilotId,
                    ship.Id,
                    ship.StationId,
                    reserve.ItemId,
                    reserve.Quantity,
                    need.Drone,
                    now,
                    CapacityAttributes()
                );
                inventoryChanged = true;
                remaining -= reserve.Quantity;
            }
            long limit = Limit(need.TypeId);
            if (remaining <= 0 || limit <= 0)
            {
                continue;
            }
            var orders = supply[need.TypeId].Where(o => o.OwnerId != pilotId && o.PriceCents <= limit).ToArray();
            foreach (var order in orders)
            {
                if (remaining <= 0)
                {
                    break;
                }
                long funds = SpendableSupplyFunds(world, pilotId, progress);
                if (funds <= 0)
                {
                    break;
                }
                int quantity = Capacity(need, (int)Math.Min(remaining, Math.Min(order.Remaining, funds / order.PriceCents)));
                if (quantity <= 0)
                {
                    continue;
                }
                var purchase = market.BuyBotRefitSupplies(
                    world,
                    catalog,
                    pilotId,
                    ship.StationId,
                    [new(need.TypeId, order.PriceCents, quantity)]
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
                        Load(world, catalog, pilotId, ship.Id, ship.StationId, stack.Id, amount, need.Drone, now, CapacityAttributes());
                        inventoryChanged = true;
                        bought -= amount;
                    }
                }
            }
        }
        bool reloaded;
        using (SessionPerformance.Phase("traffic.resupplyReload"))
        {
            // Purchases and transfers above touch station stock and cargo holds. Fitted modules
            // and their magazines are still the inputs captured for this dock transaction.
            reloaded = Reload(world, catalog, pilotId, ship.Id, magazines, now);
        }
        if (reloaded)
        {
            fit = null;
        }
        if (inventoryChanged || reloaded)
        {
            using var remaining = SessionPerformance.Phase("traffic.resupplyRemaining");
            var inventory = ReadInventory();
            magazines = ReadMagazines(database, world, catalog, pilot, inventory);
            needs = Requirements(world, catalog, pilot, inventory, magazines, needs, ref fit);
        }
        currentFit = fit;
        return progress with { Checked = true, Requirements = needs };
    }

    internal static bool Reload(WorldDatabase database, WorldAction world, CatalogDbContext catalog, long pilotId, long now)
    {
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        long ship = pilot.ActiveShipId;
        var inventory = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == ship).ToArray();
        return Reload(world, catalog, pilotId, ship, ReadMagazines(database, world, catalog, pilot, inventory), now);
    }

    private static bool Reload(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long ship,
        IReadOnlyList<SupplyMagazine> magazines,
        long now
    )
    {
        bool changed = false;
        var candidates = magazines
            .Where(m => m.Loaded < m.Capacity && !(world.Set<ModuleCooldown>().Find(m.Module)?.ReadyAt > now))
            .Select(m => new ReloadCandidate(m.Module, m.Type, checked(m.Capacity - m.Loaded)));
        foreach (var group in candidates.GroupBy(candidate => candidate.TypeId))
        {
            int type = group.Key;
            var sources = world
                .Set<OwnedItem>()
                .Where(i => i.OwnerId == pilotId && i.LocationId == ship && i.FlagId == (int)InventoryFlag.Cargo && i.TypeId == type)
                .ToArray();
            if (sources.Length > 0)
            {
                if (sources.Sum(source => (long)source.Quantity) >= group.Sum(candidate => (long)candidate.Missing))
                {
                    // Full supply preserves the existing magazine quantities and ordering while
                    // calculating the reload fit once for all magazines of this charge type.
                    PilotAmmunitionStore.Load(
                        world,
                        catalog,
                        pilotId,
                        ship,
                        group.Select(candidate => candidate.ModuleId).ToArray(),
                        sources.Select(source => source.Id).ToArray(),
                        ship,
                        now
                    );
                    changed = true;
                }
                else
                {
                    // Preserve sequential allocation when reserves cannot fill every magazine.
                    foreach (var candidate in group)
                    {
                        var available = sources
                            .Where(source => source.Quantity > 0 && source.LocationId == ship)
                            .Select(source => source.Id)
                            .ToArray();
                        if (available.Length == 0)
                        {
                            break;
                        }
                        PilotAmmunitionStore.Load(world, catalog, pilotId, ship, [candidate.ModuleId], available, ship, now);
                        changed = true;
                    }
                }
            }
        }
        return changed;
    }

    internal CapsuleerTrafficRoute? Shop(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int station,
        HullClass hull,
        ResupplyProgress progress,
        IReadOnlySet<int> avoided
    )
    {
        long funds = SpendableSupplyFunds(world, pilotId, progress);
        var types = progress.Requirements.Where(r => Limit(r.TypeId) > 0).Select(r => r.TypeId).ToArray();
        if (types.Length == 0 || funds <= 0)
        {
            return null;
        }
        int region = catalog.RequireStation(station).RegionId;
        // Search actual existing orders; shopping never manufactures a remote stock history.
        var stations = world
            .Set<NpcMarketOrder>()
            .Where(o => o.RegionId == region && !o.Bid && o.Remaining > 0 && Enumerable.Contains(types, o.TypeId))
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
                        && Enumerable.Contains(types, o.TypeId)
                    )
                    .Select(o => o.StationId)
                    .ToArray()
            )
            .Distinct()
            .Where(id => id != station);
        List<ShoppingCandidate> candidates = [];
        foreach (int target in stations)
        {
            var route = CapsuleerTrafficRoutes.ToStation(
                catalog,
                station,
                target,
                hull,
                avoided,
                RattingRules.MaximumShoppingJumps,
                policy: world.Settings.Capsuleers
            );
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

    private static long SpendableSupplyFunds(WorldAction world, long pilot, ResupplyProgress progress) =>
        CapsuleerRefitFunding.Applies(world, pilot)
            ? long.MaxValue
            : Math.Min(progress.BudgetCents - progress.SpentCents, world.Set<PilotWallet>().Find(pilot)!.Cents);

    private static void Load(
        WorldAction world,
        CatalogDbContext catalog,
        long pilot,
        long ship,
        int station,
        long item,
        int quantity,
        bool drone,
        long now,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes
    ) =>
        PilotInventoryStore.Move(
            world,
            catalog,
            pilot,
            ship,
            new([item], station, (int)(drone ? InventoryFlag.DroneBay : InventoryFlag.Cargo), quantity),
            now,
            attributes
        );
}
