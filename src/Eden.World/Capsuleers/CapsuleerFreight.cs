using System.Runtime.CompilerServices;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal static class CapsuleerFreight
{
    private sealed record Commodity(InventoryItem Item, FreightCommodity Freight);

    private sealed record Hold(InventoryFlag Flag, FreightKind Kind, double Capacity, FreightCommodity[] Goods);

    private static readonly CatalogDefinition<int[]> s_hulls = new(
        "traffic-freight-hulls",
        (catalog, _) =>
            Enum.GetValues<FreightHull>()
                .Select(h => (int)h)
                .Where(id => catalog.FittingType(id) is { GroupId: (int)HullClass.Hauler, Capacity: > 0 })
                .ToArray()
    );
    private static readonly CatalogDefinition<Commodity[]> s_goods = new("traffic-freight-goods", (catalog, _) => Goods(catalog));
    private static readonly CatalogDefinition<Hold> s_holds = new(
        "traffic-freight-holds",
        (catalog, key) =>
        {
            var kind = (FreightKind)(key % HoldKeyFactor);
            int hull = key / HoldKeyFactor;
            var preferred = FreightRules.PreferredHold(kind);
            var flag =
                preferred != InventoryFlag.Cargo && PilotInventoryStore.HoldCapacity(catalog, hull, (int)preferred) > 0
                    ? preferred
                    : InventoryFlag.Cargo;
            var definition = ShipHoldRules.Definition((int)flag)!;
            var allowed = catalog.Definition(s_goods, 0).Where(c => definition.Allows(c.Item)).ToArray();
            var goods = allowed.Where(c => Matches(kind, (InventoryCategory)c.Item.CategoryId)).ToArray();
            return new(
                flag,
                kind,
                PilotInventoryStore.HoldCapacity(catalog, hull, (int)flag),
                (goods.Length == 0 ? allowed : goods).Select(c => c.Freight).ToArray()
            );
        }
    );

    private static readonly CatalogDefinition<int[]> s_regionalHulls = new(
        "regional-freighters",
        (catalog, region) =>
        {
            var faction = (NpcEmpireFaction?)catalog.Read(CatalogData.Client.Regions, (long)region)?.FactionID;
            var hulls = catalog.Definition(s_hulls, 0);
            var local = hulls.Where(hull =>
                (FreightHull)hull switch
                {
                    FreightHull.Badger or FreightHull.Tayra => faction == NpcEmpireFaction.Caldari,
                    FreightHull.Mammoth or FreightHull.Wreathe or FreightHull.Hoarder => faction == NpcEmpireFaction.Minmatar,
                    FreightHull.Sigil or FreightHull.Bestower => faction == NpcEmpireFaction.Amarr,
                    _ => faction == NpcEmpireFaction.Gallente,
                }
            );
            return hulls.Concat(local).ToArray();
        }
    );

    private static readonly ConditionalWeakTable<CapsuleerFreightPolicy, int[]> s_customIds = new();
    private static readonly CatalogDefinition<Commodity?> s_customGoods = new(
        "traffic-custom-commodity",
        (catalog, id) => ReadCommodity(catalog, id)
    );

    private static Commodity? ReadCommodity(CatalogDbContext catalog, int id)
    {
        if (
            catalog.FittingType(id) is not { } type
            || (InventoryCategory)type.CategoryId
                is not (
                    InventoryCategory.Material
                    or InventoryCategory.Module
                    or InventoryCategory.Charge
                    or InventoryCategory.Commodity
                    or InventoryCategory.Drone
                    or InventoryCategory.Asteroid
                    or InventoryCategory.PlanetaryCommodity
                    or InventoryCategory.PlanetaryResource
                )
        )
        {
            return null;
        }
        var source = catalog.Read(CatalogData.Client.Types, (long)id);
        if (source?.Volume is not > 0 || !double.IsFinite(source.Volume.Value) || source.Published == 0)
        {
            return null;
        }
        double value = MarketSnapshotCatalog.Quotes.GetValueOrDefault(id)?.SellCents / 100d ?? source.BasePrice ?? 0;
        if (!double.IsFinite(value) || value <= 0)
        {
            return null;
        }
        var item = new InventoryItem(0, type.Id, 0, 0, (int)InventoryFlag.Cargo, 1, type.GroupId, type.CategoryId, source.Volume.Value);
        return new(item, new(type.Id, item.Volume, value));
    }

    private const int HoldKeyFactor = 16;

    internal static bool Available(CatalogDbContext catalog, CapsuleerFreightPolicy policy) =>
        catalog.Definition(s_hulls, 0).Length != 0
        && (!string.IsNullOrWhiteSpace(policy.CargoTypeIds) || catalog.Definition(s_goods, 0).Length != 0);

    internal static int Hull(CatalogDbContext catalog, long seed, int system, int slot, bool varied = true)
    {
        if (!varied && catalog.Definition(s_hulls, 0).Contains(HaulingRules.BadgerType))
        {
            return HaulingRules.BadgerType;
        }
        var hulls = catalog.Definition(s_regionalHulls, catalog.RequireSystem(system).RegionId);
        return hulls[TrafficRules.Start(seed, system, slot, hulls.Length)];
    }

    private static Commodity[] Goods(CatalogDbContext catalog)
    {
        // An allowlist keeps story items, officer modules and capital components out of generated cargo.
        string[] names =
        [
            "Tritanium",
            "Pyerite",
            "Mexallon",
            "Isogen",
            "Nocxium",
            "Scordite",
            "Veldspar",
            "Pyroxeres",
            "Electronic Parts",
            "Mechanical Parts",
            "Consumer Electronics",
            "Robotics",
            "Water",
            "Oxygen",
            "Coolant",
            "Nanite Repair Paste",
            "Small Shield Extender I",
            "Damage Control II",
            "Warp Disruptor II",
            "Hobgoblin I",
            "Warrior I",
            "EMP S",
            "Antimatter Charge S",
            "Scourge Light Missile",
            "Fusion M",
            "Cap Booster 200",
        ];
        var accepted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int ore = CapsuleerProvisioningCatalog.FreightOre(catalog);
        var candidates = catalog.Definitions.Items.Values.Where(i => accepted.Contains(i.Name) || i.Id == ore).OrderBy(i => i.Id);
        return candidates.Select(c => ReadCommodity(catalog, c.Id)).OfType<Commodity>().ToArray();
    }

    private static bool Matches(FreightKind kind, InventoryCategory category) =>
        kind switch
        {
            FreightKind.Ore => category == InventoryCategory.Asteroid,
            FreightKind.Minerals => category == InventoryCategory.Material,
            FreightKind.Planetary => category is InventoryCategory.PlanetaryResource or InventoryCategory.PlanetaryCommodity,
            FreightKind.Ammunition => category == InventoryCategory.Charge,
            FreightKind.Bulk => category
                is InventoryCategory.Asteroid
                    or InventoryCategory.Material
                    or InventoryCategory.PlanetaryCommodity,
            FreightKind.Courier => category is InventoryCategory.Module or InventoryCategory.Drone,
            _ => category is InventoryCategory.Commodity or InventoryCategory.Charge or InventoryCategory.Drone,
        };

    internal static void Prepare(CatalogDbContext catalog, int hull)
    {
        if (catalog.FittingType(hull)?.GroupId == (int)HullClass.Hauler)
        {
            _ = catalog.Definition(s_holds, checked(hull * HoldKeyFactor + (int)FreightRules.Kind((FreightHull)hull, 0)));
            _ = catalog.Definition(s_holds, checked(hull * HoldKeyFactor + (int)FreightRules.Kind((FreightHull)hull, 99)));
        }
    }

    internal static FreightManifest? Load(
        WorldAction world,
        CatalogDbContext catalog,
        long pilot,
        long ship,
        int hull,
        int destination,
        long sequence,
        bool emptyHull = false
    )
    {
        if (catalog.FittingType(hull)?.GroupId != (int)HullClass.Hauler)
        {
            return null;
        }
        ulong seed = TravelerRoutineRules.Mix(unchecked((ulong)pilot)) ^ (uint)destination;
        var policy = world.Settings.Capsuleers.Freight;
        var kind = FreightRules.Kind((FreightHull)hull, seed, policy.CourierPercent);
        var hold = catalog.Definition(s_holds, checked(hull * HoldKeyFactor + (int)kind));
        int flag = (int)hold.Flag;
        double multiplier = hold.Flag == InventoryFlag.GeneralMiningHold ? (double)world.Settings.MiningHolds.GeneralMultiplier : 1;
        double free = emptyHull
            ? hold.Capacity * multiplier
            : PilotInventoryStore.HoldCapacity(world, catalog, pilot, ship, flag)
                - InventoryPersistence.UsedVolume(world, catalog, pilot, ship, flag);
        var goods = hold.Goods;
        if (!string.IsNullOrWhiteSpace(policy.CargoTypeIds))
        {
            var allowed = ShipHoldRules.Definition(flag)!;
            goods = s_customIds
                .GetValue(policy, p => p.TypeIds())
                .Select(id => catalog.Definition(s_customGoods, id))
                .OfType<Commodity>()
                .Where(c => allowed.Allows(c.Item))
                .Select(c => c.Freight)
                .ToArray();
        }
        var load = FreightRules.Load(seed, sequence, hold.Kind, goods, free, policy);
        List<FreightStack> stacks = [];
        if (load.Length > 0)
        {
            long id = InventoryPersistence.NextId(world);
            foreach (var item in load)
            {
                world.Add(
                    new OwnedItem
                    {
                        Id = id,
                        TypeId = item.TypeId,
                        Quantity = item.Quantity,
                        LocationId = ship,
                        OwnerId = pilot,
                        FlagId = flag,
                    }
                );
                stacks.Add(new(id++, item.TypeId, item.Quantity));
            }
            world.SaveChanges();
        }
        return new(sequence, destination, stacks.ToArray());
    }

    internal static bool Upgrade(WorldAction world, CatalogDbContext catalog, long pilotId, long shipId)
    {
        var starter = world.Set<OwnedItem>().Find(shipId + 1);
        if (
            starter is not { Quantity: FreightRules.LegacyOreUnits, Singleton: false, FlagId: (int)InventoryFlag.Cargo }
            || starter.OwnerId != pilotId
            || starter.LocationId != shipId
            || starter.TypeId != CapsuleerProvisioningCatalog.FreightOre(catalog)
        )
        {
            return false;
        }
        var agent = world.Set<SavedCapsuleerAgent>().Find(pilotId);
        var pilot = world.Pilots.Find(pilotId);
        var ship = world.Ships.Find(shipId);
        if (
            agent?.FounderShipId != shipId
            || pilot?.ActiveShipId != shipId
            || ship?.OwnerId != pilotId
            || ship.TypeId != HaulingRules.BadgerType
            || ship.Name != pilot.Name
            || world.Positions.Find(shipId) is not null
            || pilot.AccountId != CapsuleerCommunityStore.MinerAccountId
            || world.Set<SavedTrafficIdentity>().Find(pilotId) is not null
            || world.Filter<ShipLoss>(loss => loss.ShipId == shipId).Any()
        )
        {
            return false;
        }
        if (world.Set<SavedCapsuleerProvisioning>().Find(1) is not { } provisioning)
        {
            return false;
        }
        long seed = provisioning.WorldSeed;
        if (
            !CapsuleerProvisioningRules.MatchesName(
                seed,
                agent.CommunitySystemId,
                agent.Career,
                agent.Slot,
                pilot.Name,
                Math.Min(world.PilotCount + 1, CapsuleerProvisioningRules.MaximumNameAttempts)
            )
        )
        {
            return false;
        }
        var health = world.Set<ShipHealthState>().Find(shipId);
        if (
            health is not null
            && (
                health.ArmorDamage > 0
                || health.StructureDamage > 0
                || health.ShieldCharge + InventoryDefinitions.ResourceTolerance
                    < PilotFittingStore.ShipAttributes(world, catalog, pilot).GetValueOrDefault(DogmaAttributeId.ShieldCapacity)
            )
        )
        {
            return false;
        }
        if (world.Filter<OwnedItem>(i => i.LocationId == shipId).Any(i => i.FlagId != (int)InventoryFlag.Cargo))
        {
            return false;
        }
        int hull = Hull(catalog, seed, agent.CommunitySystemId, agent.Slot, world.Settings.Capsuleers.Freight.VariedHulls);
        double remaining =
            InventoryPersistence.UsedVolume(world, catalog, pilotId, shipId, (int)InventoryFlag.Cargo)
            - starter.Quantity * PilotInventoryStore.Volume(catalog, starter.TypeId);
        if (remaining > catalog.RequireItem(hull).Capacity.GetValueOrDefault())
        {
            return false;
        }
        world.Remove(starter);
        if (hull != ship.TypeId)
        {
            ship.TypeId = hull;
            CapsuleerCommunityStore.GrantRequirements(world, catalog, pilotId, hull, []);
            if (health is not null)
            {
                world.Remove(health);
            }
            if (world.Set<ShipEnergy>().Find(shipId) is { } energy)
            {
                world.Remove(energy);
            }
        }
        world.SaveChanges();
        return true;
    }

    internal static void Deliver(WorldAction world, long pilot, long ship, FreightManifest freight)
    {
        foreach (var stack in freight.Stacks)
        {
            if (
                world.Set<OwnedItem>().Find(stack.ItemId) is not { } item
                || item.OwnerId != pilot
                || item.LocationId != ship
                || item.TypeId != stack.TypeId
            )
            {
                continue;
            }
            if (item.Quantity <= stack.Quantity)
            {
                world.Remove(item);
            }
            else
            {
                item.Quantity -= stack.Quantity;
            }
        }
        world.SaveChanges();
    }
}
