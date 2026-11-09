using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Economy;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Data.World.Skills;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Reprocessing;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Economy;
using Eden.World.Fittings;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Industry;

public sealed class StarterProductionTests
{
    internal const long Producer = 90000001;
    internal const long Consumer = 90000002;
    internal const int Home = 60003760;
    internal const int System = 30000142;
    private const int Charge = 215;
    private const int RoundsPerFit = 10_000;
    private const long Attacker = 4_000_000_000_001;

    internal sealed record Ingredient(int TypeID, int Quantity);

    internal sealed record Recipe(int BlueprintId, int Time, IReadOnlyList<Ingredient> Materials, IReadOnlyList<Ingredient> Products);

    internal sealed record Supplier(int CorporationId, int StationId, IReadOnlyList<int> Types);

    internal sealed record Ore(int OreTypeId, long BeltId, int Count, IReadOnlyList<OreMaterial> Materials);

    internal sealed record OreMaterial(int MaterialTypeID, int Quantity);

    internal sealed record Audit(
        int Build,
        IReadOnlyList<int> Hulls,
        Dictionary<int, int[]> Fits,
        IReadOnlyList<Recipe> Recipes,
        IReadOnlyList<Supplier> Suppliers,
        IReadOnlyList<Ore> OreSources
    );

    internal static Audit ReadAudit()
    {
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream("Eden.Protocol.Tests.Fixtures.StarterProductionAudit.json")!;
        return JsonSerializer.Deserialize<Audit>(stream, JsonSerializerOptions.Web)!;
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly ResidentFitCache _fits;
        private readonly Random _rolls = new(3662168);
        internal TestClock Clock { get; } = new();
        internal TestWorld Db { get; }
        internal Audit Data { get; } = ReadAudit();
        internal string CatalogPath => new WorldDatabase(Db.Path).CatalogPath;
        internal IPilotIndustry Industry => Db.Services.GetRequiredService<IPilotIndustry>();
        internal IPilotMarket Market => Db.Services.GetRequiredService<IPilotMarket>();
        internal IPersonalMarket Personal => Db.Services.GetRequiredService<IPersonalMarket>();
        internal IPilotInventory Inventory => Db.Services.GetRequiredService<IPilotInventory>();
        internal IPilotFitting Fitting => Db.Services.GetRequiredService<IPilotFitting>();
        internal IPilotFleet Fleet => Db.Services.GetRequiredService<IPilotFleet>();

        internal Fixture(bool trained = true, bool resident = false)
        {
            Db = new(clock: Clock, liveWorld: resident);
            PassiveCatalogFixture.LoadStarterProduction(CatalogPath);
            _fits = new(new(Db.Path));
            using (CatalogDbContext catalog = new(CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[5,7,14,17]}}');
                    """
                );
                var original = catalog.Set<Station>().Single(s => s.Id == Home);
                foreach (var supplier in Data.Suppliers)
                {
                    if (!catalog.Set<NpcCorporation>().Any(c => c.Id == supplier.CorporationId))
                    {
                        catalog.Add(new NpcCorporation { Id = supplier.CorporationId, Name = $"Supplier {supplier.CorporationId}" });
                    }
                    if (!catalog.Set<Station>().Any(s => s.Id == supplier.StationId))
                    {
                        catalog.Add(
                            new Station
                            {
                                Id = supplier.StationId,
                                TypeId = original.TypeId,
                                OwnerId = supplier.CorporationId,
                                OperationId = original.OperationId,
                                SolarSystemId = original.SolarSystemId,
                                OrbitId = original.OrbitId,
                                X = original.X,
                                Y = original.Y,
                                Z = original.Z,
                            }
                        );
                    }
                }
                string station = JsonSerializer.Serialize(
                    new
                    {
                        operationID = original.OperationId,
                        reprocessingEfficiency = 0.5,
                        reprocessingStationsTake = 0.05,
                    }
                );
                catalog.Database.ExecuteSql(
                    $"INSERT OR REPLACE INTO SourceRecord VALUES ({"sde/npcStations"},{Home.ToString()},{station})"
                );
                catalog.SaveChanges();
            }
            using WorldDbContext world = new(Db.Path);
            world.Set<SavedWorldEconomy>().Single().TestingMarket = false;
            world.Set<SavedWorldEconomy>().Single().TestingGrants = false;
            // Established pilots and earned capital are fixture inputs. No ore,
            // minerals, blueprints, finished hulls or fittings are granted.
            foreach (long id in new[] { Producer, Consumer })
            {
                world.Add(new PilotWallet { PilotId = id, Cents = 20_000_000_000 });
                if (trained)
                {
                    using CatalogDbContext catalog = new(CatalogPath);
                    foreach (
                        int skill in catalog
                            .Set<ItemType>()
                            .Where(t => t.Group.CategoryId == (int)InventoryCategory.Skill)
                            .Select(t => t.Id)
                            .ToArray()
                    )
                    {
                        double rank = catalog.Set<TypeAttribute>().Find(skill, (int)DogmaAttributeId.SkillTimeConstant)!.Value;
                        var saved =
                            world.Set<PilotSkill>().Find(id, skill) ?? world.Add(new PilotSkill { PilotId = id, TypeId = skill }).Entity;
                        saved.Points = TrainedSkill.PointsForLevel(rank, 5);
                    }
                }
            }
            world.Ships.Single(s => s.OwnerId == Consumer).StationId = Home;
            world.SaveChanges();
            if (trained)
            {
                var capsule = Fleet.Leave(Producer, Pilot().ShipId).Pilot;
                Fleet.RecoverCorvette(Producer, capsule.ShipId, Home);
            }
        }

        internal DockedPilot Pilot(long id = Producer) => Db.Store.FindPilot(id == Producer ? 1000001 : 1000002)!;

        internal void EnableServiceCosts()
        {
            using WorldDbContext world = new(Db.Path);
            using CatalogDbContext catalog = new(CatalogPath);
            world.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
            EconomicStore.SeedPrices(world, catalog, new EconomicConfiguration());
            world.SaveChanges();
        }

        internal void At(long pilotId, int station)
        {
            Db.Store.SetPosition(Pilot(pilotId).ShipId, null);
            using WorldDbContext world = new(Db.Path);
            world.Ships.Find(Pilot(pilotId).ShipId)!.StationId = station;
            world.SaveChanges();
        }

        internal InventoryItem Stack(long pilotId, int typeId, int station = Home) =>
            Inventory.List(pilotId, station).Single(i => i.TypeId == typeId && !i.Singleton);

        internal long Quantity(long pilotId, int typeId, int station = Home) =>
            Inventory.List(pilotId, station).Where(i => i.TypeId == typeId).Sum(i => (long)i.Quantity);

        internal long BuyInput(int typeId)
        {
            var supplier = Data.Suppliers.First(s => s.Types.Contains(typeId));
            At(Producer, supplier.StationId);
            var order = Market.Orders(Producer, typeId).Single(o => o.StationId == supplier.StationId && !o.Bid);
            Assert.True(order.PriceCents > TestingMarketRules.PriceCents);
            Market.Buy(Producer, supplier.StationId, [new(typeId, order.PriceCents, 1)]);
            var item = Stack(Producer, typeId, supplier.StationId);
            // Simulated travel/cargo transfer between supplier counters and home.
            // The market, inventory and capacity operations remain production owners.
            Inventory.Move(Producer, [item.Id], supplier.StationId, Pilot().ShipId, (int)InventoryFlag.Cargo, null);
            At(Producer, Home);
            Inventory.Move(Producer, [item.Id], Pilot().ShipId, Home, (int)InventoryFlag.Hangar, null);
            return Stack(Producer, typeId).Id;
        }

        internal void Mine(int oreType, int required, long? targetId = null)
        {
            var mining = Db.Services.GetRequiredService<IPilotMining>();
            long ship = Pilot().ShipId;
            long module = Inventory.List(Producer, ship).Single(i => i.TypeId == (int)MiningLaserType.Civilian).Id;
            long rock = targetId ?? AsteroidDefinitions.FirstAsteroidId + oreType;
            SpacePosition miningPosition;
            using (WorldDbContext world = new(Db.Path))
            {
                if (world.Set<WorldAsteroid>().Find(rock) is null)
                {
                    world.Add(
                        new WorldAsteroid
                        {
                            Id = rock,
                            TypeId = oreType,
                            SystemId = System,
                            BeltId = checked((int)Data.OreSources.Single(o => o.OreTypeId == oreType).BeltId),
                            Quantity = 1_000_000,
                            Radius = 100,
                        }
                    );
                    world.SaveChanges();
                }
                var deposit = world.Set<WorldAsteroid>().Find(rock)!;
                miningPosition = new(deposit.X, deposit.Y, deposit.Z);
            }
            long mined = Quantity(Producer, oreType);
            bool inSpace = false;
            int emptyCycles = 0;
            while (mined < required)
            {
                if (!inSpace)
                {
                    Db.Store.SetPosition(ship, miningPosition, System);
                    inSpace = true;
                }
                var start = mining.Begin(Producer, module, rock, Clock.Now);
                Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(start.Laser.DurationMilliseconds));
                var result = mining.Collect(Producer, module, rock, new(1, true, new(_rolls.NextDouble(), .999999, .5, .999999, .5)));
                emptyCycles = result.Extracted > 0 ? 0 : emptyCycles + 1;
                Assert.True(emptyCycles < 100, "Seeded mining must eventually collect a whole ore unit.");
                mined += result.Extracted + result.CriticalBonus;
                if (result.CargoFull || mined >= required)
                {
                    Db.Store.SetPosition(ship, null);
                    inSpace = false;
                    var cargo = Inventory.List(Producer, ship).Where(i => i.TypeId == oreType && ShipHoldRules.IsHold(i.FlagId)).ToArray();
                    Inventory.Move(Producer, cargo.Select(i => i.Id).ToArray(), ship, Home, (int)InventoryFlag.Hangar, null);
                    Db.Services.GetRequiredService<IPilotInventoryStacks>().StackAll(Producer, Home, (int)InventoryFlag.Hangar);
                }
            }
        }

        internal void Gather(IReadOnlyDictionary<int, int> materials)
        {
            var refining = Db.Services.GetRequiredService<IPilotReprocessing>();
            int[] ores = [1230, 1228, 18, 20, 21, 1223, 22];
            int[] minerals = [34, 35, 36, 37, 38, 39, 40];
            for (int index = 0; index < minerals.Length; index++)
            {
                int missing = checked((int)Math.Max(0, materials.GetValueOrDefault(minerals[index]) - Quantity(Producer, minerals[index])));
                if (missing == 0)
                {
                    continue;
                }
                int ore = ores[index];
                Mine(ore, 100);
                long id = Stack(Producer, ore).Id;
                var preview = Assert.Single(refining.Quote(Producer, Home, Pilot().ShipId, [id]).Items);
                int perPortion = preview.Recoverables.Single(m => m.TypeId == minerals[index]).Quantity / preview.Portions;
                int portions = checked((int)Math.Ceiling(missing / (double)perPortion));
                Mine(ore, checked(portions * 100));
                id = Stack(Producer, ore).Id;
                refining.Reprocess(Producer, Home, [id], Home, Producer, Home, (int)InventoryFlag.Hangar);
            }
            Assert.All(materials, m => Assert.True(Quantity(Producer, m.Key) >= m.Value));
        }

        internal void Manufacture(Recipe recipe, int runs)
        {
            long blueprint = BuyInput(recipe.BlueprintId);
            var request = new ManufacturingRequest(
                blueprint,
                recipe.BlueprintId,
                Home,
                runs,
                recipe.Products.Single().TypeID,
                Math.Round(recipe.Time * runs * 0.8, MidpointRounding.ToEven),
                recipe.Materials.ToDictionary(m => m.TypeID, m => checked(m.Quantity * runs))
            );
            var installed = Industry.Install(Producer, request);
            WorldInitialization.Initialize(new(Db.Path), Clock);
            Clock.AdvanceWallTime(TimeSpan.FromSeconds(installed.Job.DurationSeconds));
            var delivered = Industry.Deliver(Producer, installed.Job.Id, System);
            Assert.Equal(recipe.Products.Single().Quantity * runs, Assert.Single(delivered.Changes).Item.Quantity);
            Assert.Empty(Industry.Deliver(Producer, installed.Job.Id, System).Changes);
        }

        internal long Fit(int hull, IReadOnlyList<int> fit)
        {
            var packaged = Stack(Consumer, hull);
            long ship = Assert.Single(Fleet.Assemble(Consumer, [packaged.Id], null).ShipIds);
            Fleet.Board(Consumer, ship, Pilot(Consumer).ShipId);
            foreach (int type in fit.Where(type => type != Charge))
            {
                var changes = Fitting.Fit(Consumer, [Stack(Consumer, type).Id], Home, (int)InventoryFlag.AutoFit);
                long module = changes.Single(c => InventoryDefinitions.IsFittedSlot(c.Item.FlagId)).Item.Id;
                Fitting.SetOnline(Consumer, ship, module, true);
            }
            var attributes = Fitting.ShipAttributes(Consumer);
            Assert.True(attributes[DogmaAttributeId.CpuLoad] <= attributes[DogmaAttributeId.CpuOutput]);
            Assert.True(attributes[DogmaAttributeId.PowerLoad] <= attributes[DogmaAttributeId.PowerOutput]);
            if (fit.Contains(Charge))
            {
                var guns = Inventory.List(Consumer, ship).Where(i => i.TypeId == 561).Select(i => i.Id).ToArray();
                var ammo = Db.Services.GetRequiredService<IPilotAmmunition>();
                ammo.Load(Consumer, ship, guns, [Stack(Consumer, Charge).Id], Home, Clock.Now);
                Assert.All(ammo.Read(Consumer, ship), magazine => Assert.True(magazine.Quantity > 0));
                Assert.Equal(3, ammo.Read(Consumer, ship).Count);
            }
            return ship;
        }

        internal void Lose(long shipId)
        {
            Db.Store.SetPosition(shipId, SpacePosition.Zero, System);
            using WorldDbContext world = new(Db.Path);
            using var transaction = world.Database.BeginTransaction();
            using CatalogDbContext catalog = new(CatalogPath);
            var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == Consumer);
            world.Add(
                new WorldNpc
                {
                    Id = Attacker,
                    TypeId = 601,
                    CorporationId = 1000035,
                    SystemId = System,
                }
            );
            world.SaveChanges();
            var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, Consumer));
            Assert.True(
                PilotHealthStore.Apply(world, shipId, defense, new DamageVector(1_000_000, 0, 0, 0), Clock.Now).Remaining.Destroyed
            );
            ShipLossPersistence.Apply(
                world,
                catalog,
                pilot,
                Attacker,
                shipId,
                System,
                pilot.ActiveShip.Position!,
                Clock.Now,
                new DestroyLoot()
            );
            world.SaveChanges();
            transaction.Commit();
            Assert.True(Db.Services.GetRequiredService<IPilotHealth>().WasDestroyed(shipId));
            At(Consumer, Home);
        }

        private sealed class DestroyLoot : IShipLootRoll
        {
            public bool Survives() => false;
        }

        public void Dispose()
        {
            _fits.Dispose();
            Db.Dispose();
        }
    }

    [Theory]
    [InlineData(603)]
    [InlineData(32880)]
    [InlineData(648)]
    public void MinedInputsBuildTradeFitLoseAndReplaceEachStarterRoleWithoutDevelopmentPurchases(int hull)
    {
        using Fixture f = new();
        f.EnableServiceCosts();
        var fit = f.Data.Fits[hull];
        var quantities = fit.Append(hull).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Key == Charge ? RoundsPerFit * 2 : g.Count() * 2);
        var jobs = quantities
            .Select(q =>
            {
                var recipe = f.Data.Recipes.Single(r => r.Products.Single().TypeID == q.Key);
                return (Recipe: recipe, Runs: checked((int)Math.Ceiling(q.Value / (double)recipe.Products.Single().Quantity)));
            })
            .ToArray();
        var required = jobs.SelectMany(j => j.Recipe.Materials.Select(m => new Ingredient(m.TypeID, m.Quantity * j.Runs)))
            .GroupBy(m => m.TypeID)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Quantity));
        f.Gather(required);
        foreach (var job in jobs)
        {
            f.Manufacture(job.Recipe, job.Runs);
        }
        foreach (var item in quantities)
        {
            var stack = f.Stack(Producer, item.Key);
            long price =
                item.Key == hull ? 200_000_000
                : item.Key == Charge ? 1000
                : 1_000_000;
            // Claimed production now enables NPC competition. Keep this personal
            // inventory/ownership scenario's listing ahead of the snapshot ask.
            price = Math.Min(price, MarketSnapshotCatalog.Quotes[item.Key].SellCents!.Value - 1);
            f.Personal.PlaceSell(Producer, [new(stack.Id, Home, item.Key, price, item.Value)], 90);
            // The producer never creates a selected client connection.
            f.Market.Buy(Consumer, Home, [new(item.Key, price, item.Value)]);
        }
        Assert.Empty(f.Personal.PersonalOrders(Producer));
        long first = f.Fit(hull, fit);
        f.Lose(first);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        long replacement = f.Fit(hull, fit);
        Assert.NotEqual(first, replacement);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(replacement, f.Pilot(Consumer).ShipId);
        Assert.Equal(hull, f.Pilot(Consumer).Ship.TypeId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<ShipLoss>().Where(l => l.PilotId == Consumer));
        Assert.All(saved.Set<NpcMarketOrder>(), order => Assert.NotEqual(NpcMarketKind.Development, order.Kind));
        Assert.Empty(saved.Set<TestingWalletGrant>());
        Assert.All(quantities, q => Assert.Equal(q.Value, Assert.Single(f.Market.History(Consumer, [q.Key])).Volume));
    }

    [Fact]
    public void CatalogAuditHasRecipesSuppliersAndFixedPopulatedOreSourcesForEveryInput()
    {
        using Fixture f = new();
        Assert.Equal(3662168, f.Data.Build);
        Assert.All(f.Data.Hulls, hull => Assert.Contains(hull, f.Industry.ProductTypes()));
        Assert.Contains((int)StarterProductionType.DamageControl, f.Industry.ProductTypes());
        Assert.Contains((int)StarterProductionType.ExpandedCargohold, f.Industry.ProductTypes());
        Assert.DoesNotContain(601, f.Industry.ProductTypes());
        var supplied = f.Data.Suppliers.SelectMany(s => s.Types).ToHashSet();
        Assert.All(f.Data.Recipes, r => Assert.Contains(r.BlueprintId, supplied));
        var minerals = f.Data.OreSources.SelectMany(o => o.Materials).Select(m => m.MaterialTypeID).ToHashSet();
        Assert.All(f.Data.Recipes.SelectMany(r => r.Materials), material => Assert.Contains(material.TypeID, minerals));
        Assert.All(f.Data.OreSources, ore => Assert.True(ore.Count > 0));
        using CatalogDbContext catalog = new(f.CatalogPath);
        foreach (var recipe in f.Data.Recipes)
        {
            var raw = catalog
                .Set<SourceRecord>()
                .Find("client/blueprints", JsonSerializer.Serialize(recipe.BlueprintId.ToString(CultureInfo.InvariantCulture)))!;
            Assert.Equal(recipe.Time, raw.Read<ClientBlueprints>().Activities!.Manufacturing!.Time);
        }
    }

    [Fact]
    public void InstitutionalRestockingKeepsEnteredVolumeConsistentWithSavedConfiguration()
    {
        using Fixture f = new(trained: false);
        f.BuyInput((int)IndustrySkill.Industry);
        var supplier = f.Data.Suppliers.First(s => s.Types.Contains((int)IndustrySkill.Industry));
        long[] identities;
        using (WorldDbContext world = new(f.Db.Path))
        {
            identities = world
                .Set<NpcMarketOrder>()
                .Where(o => o.Kind == NpcMarketKind.Institutional)
                .OrderBy(o => o.Id)
                .Select(o => o.Id)
                .ToArray();
            world.Set<SavedWorldEconomy>().Single().InstitutionalStock = 2000;
            world.SaveChanges();
        }
        f.At(Producer, supplier.StationId);
        var orders = f.Market.Orders(Producer).Where(o => identities.Contains(o.Id)).ToArray();
        Assert.All(
            orders,
            order =>
            {
                Assert.Equal(2000, order.Remaining);
                Assert.Equal(2000, order.Entered);
            }
        );
        Assert.Equal(identities, orders.Select(o => o.Id));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(2000, f.Personal.Economy().InstitutionalStock);
    }

    [Fact]
    public void InstitutionalSupplySellsOnlyCatalogBlueprintsAndSkillbooksAndSkillsCanBeInjected()
    {
        using Fixture f = new(trained: false);
        using (WorldDbContext settings = new(f.Db.Path))
        {
            settings.Set<SavedWorldEconomy>().Single().InstitutionalStock = 25;
            settings.SaveChanges();
        }
        long book = f.BuyInput((int)IndustrySkill.Industry);
        var result = f.Db.Services.GetRequiredService<IPilotSkillBooks>().Inject(Producer, [book]);
        Assert.Contains((int)IndustrySkill.Industry, result.TypeIds);
        using WorldDbContext world = new(f.Db.Path);
        Assert.NotNull(world.Set<PilotSkill>().Find(Producer, (int)IndustrySkill.Industry));
        using CatalogDbContext catalog = new(f.CatalogPath);
        Assert.All(
            world.Set<NpcMarketOrder>().Where(o => o.Kind == NpcMarketKind.Institutional),
            order =>
            {
                var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == order.TypeId);
                Assert.Contains(type.Group.CategoryId, new[] { (int)InventoryCategory.Blueprint, (int)InventoryCategory.Skill });
                Assert.Equal(25, order.Remaining);
            }
        );
        Assert.Throws<InvalidDataException>(() => f.Market.Buy(Producer, Home, [new(603, long.MaxValue, 1)]));
    }
}
