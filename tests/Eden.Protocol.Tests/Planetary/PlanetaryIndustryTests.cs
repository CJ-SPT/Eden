using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Planetary;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Market;
using Eden.Gameplay.Planetary;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Loot;
using Eden.World.Planetary;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Planetary;

public sealed class PlanetaryIndustryTests
{
    internal const int PlanetId = 45000001;
    internal const int CommandCenter = 2524;
    internal const int Storage = 2541;
    internal const int Launchpad = 2544;
    internal const int Extractor = 2848;
    internal const int Processor = 2473;
    internal const int WaterSchematic = 121;
    internal const int Water = 3645;

    internal static Fixture Prepare()
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadPlanetary(f.CatalogPath);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            int system = catalog.RequireStation(checked((int)f.StationId)).SolarSystemId;
            catalog.Database.ExecuteSql($"UPDATE SolarSystem SET Name='Colony test system' WHERE Id={system}");
            catalog.Database.ExecuteSql(
                $"INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES({PlanetId},{system},2016,7,6000000,0,0,0)"
            );
            string definition = "{\"celestialIndex\":1,\"radius\":6000000,\"typeID\":2016}";
            catalog.Database.ExecuteSql($"INSERT INTO SourceRecord VALUES('sde/mapPlanets',{PlanetId.ToString()},{definition})");
        }
        f.Train(PlanetaryRules.RemoteSensingSkill, 5);
        f.Train(PlanetaryRules.PlanetologySkill, 5);
        f.Train(PlanetaryRules.AdvancedPlanetologySkill, 5);
        f.Train(PlanetaryRules.CommandCenterUpgradesSkill, 5);
        f.Train(PlanetaryRules.InterplanetaryConsolidationSkill, 5);
        using (WorldDbContext world = new(f.Db.Path))
        {
            long account = world.Pilots.Single(p => p.Id == f.PilotId).AccountId;
            world.Set<Account>().Find(account)!.CloneGrade = AccountCloneGrade.Omega;
            world.Add(new PilotWallet { PilotId = f.PilotId, Cents = 10_000_000_000 });
            world.SaveChanges();
        }
        f.Db.Store.SetPosition(f.ShipId, new(10_000_000, 0, 0));
        return f;
    }

    internal static IPilotPlanetaryIndustry Industry(Fixture f) => f.Db.Services.GetRequiredService<IPilotPlanetaryIndustry>();

    internal static PlanetaryCatalog Catalog(Fixture f)
    {
        using CatalogDbContext catalog = new(f.CatalogPath);
        return PlanetaryCatalogReader.Read(catalog);
    }

    [Fact]
    public void BotProductionSitesBatchPlanetRecordsAndRetainTheCatalogResults()
    {
        const int Source = 30000144;
        const int Restricted = 30000142;
        const int Nearby = 30000145;
        const int FirstPlanet = 45001000;
        const int PlanetsPerSystem = 12;
        using TestWorld world = new();
        string path = new WorldDatabase(world.Path).CatalogPath;
        using (CatalogDbContext edit = new(path, writable: true))
        {
            edit.Add(
                new SolarSystem
                {
                    Id = Nearby,
                    ConstellationId = 20000020,
                    Name = "Factory test system",
                }
            );
            foreach (int system in new[] { Source, Restricted, Nearby })
            {
                for (int index = 0; index < PlanetsPerSystem; index++)
                {
                    int id = FirstPlanet + (system - Source) * PlanetsPerSystem + index;
                    edit.Add(
                        new Celestial
                        {
                            Id = id,
                            SolarSystemId = system,
                            TypeId = PlanetaryResources.Barren,
                            GroupId = 7,
                            Radius = index == 0 ? 0 : 5_000_000,
                            X = index + 100,
                        }
                    );
                    if (index != 1)
                    {
                        edit.Add(
                            new SourceRecord
                            {
                                Dataset = CatalogData.Sde.MapPlanets.Name,
                                Key = id.ToString(CultureInfo.InvariantCulture),
                                Data = JsonSerializer.Serialize(new SdeMapPlanets { CelestialIndex = index, Radius = 6_000_000 }),
                            }
                        );
                    }
                }
            }
            edit.SaveChanges();
        }

        using var operation = new CatalogReadScope().Begin();
        using CatalogDbContext catalog = new(path);
        _ = catalog.Definitions.Systems;
        using (var cold = SessionPerformance.Capture("cold production sites", _ => { }))
        {
            var sites = PlanetaryCatalogReader.ProductionSites(catalog, [Source, Restricted, Nearby]);
            Assert.Equal(2, cold.Commands);
            Assert.Equal(PlanetsPerSystem - 2, sites[Source].Length);
            Assert.Equal(PlanetsPerSystem - 2, sites[Nearby].Length);
            Assert.Empty(sites[Restricted]);
            Assert.Equal(new(FirstPlanet + 2, Source, PlanetaryResources.Barren, 2, 5_000_000, new(102, 0, 0), true), sites[Source][0]);
        }

        using var warm = SessionPerformance.Capture("warm production sites", _ => { });
        var repeated = PlanetaryCatalogReader.ProductionSites(catalog, [Nearby, Source, Restricted]);
        Assert.Equal(FirstPlanet + 2, repeated[Source][0].Id);
        Assert.Equal(0, warm.Commands);
        Assert.Equal(0, warm.Connections);
    }

    [Fact]
    public void BotProductionPlansEveryCatalogCommodityFromExtraction()
    {
        using var fixture = Prepare();
        var catalog = Catalog(fixture);
        Assert.Equal(68, catalog.Schematics.Count);
        Assert.Equal(15, catalog.CommodityTiers.Count(row => row.Value == 0));
        foreach (var commodity in catalog.CommodityTiers.Keys)
        {
            Dictionary<int, long> stock = [];
            int turns = 0;
            while (stock.GetValueOrDefault(commodity) < 1)
            {
                Assert.True(turns++ < 500, $"Production did not converge for {commodity}.");
                var step = Assert.IsType<PlanetaryProductionStep>(PlanetaryProductionPlan.Next(catalog, commodity, 1, stock, 1));
                foreach (var input in step.Inputs)
                {
                    Assert.True(stock.GetValueOrDefault(input.TypeId) >= input.Quantity);
                    stock[input.TypeId] -= input.Quantity;
                }
                stock[step.TypeId] = stock.GetValueOrDefault(step.TypeId) + step.Quantity;
            }
        }
    }

    internal static PlanetaryColony Deploy(Fixture f, bool factory = true)
    {
        long center = f.Grant(CommandCenter, cargo: true);
        List<PlanetaryCommand> commands =
        [
            new CreatePlanetaryPin(new(center), CommandCenter, 1, 1),
            new UpgradePlanetaryCommand(new(center), 4),
        ];
        if (factory)
        {
            commands.AddRange([
                new CreatePlanetaryPin(new(1, 1), Launchpad, 1, 1.001),
                new CreatePlanetaryPin(new(2, 1), Extractor, 1, 1.002),
                new CreatePlanetaryPin(new(3, 1), Processor, 1, 1.003),
                new AddPlanetaryHead(new(2, 1), new(0, 1, 1.002)),
                new CreatePlanetaryLink(new(1, 1), new(2, 1), 0),
                new CreatePlanetaryLink(new(1, 1), new(3, 1), 0),
                new SetPlanetarySchematic(new(3, 1), WaterSchematic),
                new InstallPlanetaryProgram(new(2, 1), PlanetaryResources.AqueousLiquids, PlanetaryRules.MinimumHeadRadius),
                new CreatePlanetaryRoute(new(1, 2), [new(2, 1), new(1, 1)], PlanetaryResources.AqueousLiquids, 6000),
                new CreatePlanetaryRoute(new(2, 2), [new(1, 1), new(3, 1)], PlanetaryResources.AqueousLiquids, 3000),
                new CreatePlanetaryRoute(new(3, 2), [new(3, 1), new(1, 1)], Water, 20),
            ]);
        }
        return Industry(f).Update(f.PilotId, PlanetId, commands).Colony;
    }

    [Fact]
    public async Task SchematicChangesPreservePlanetaryTransitRoutes()
    {
        using var f = Prepare();
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var colony = Deploy(f);
        var industry = Industry(f);
        long port = colony.Pins.Single(p => p.TypeId == Launchpad).Id;
        long producer = colony.Pins.Single(p => p.TypeId == Processor).Id;
        PlanetaryId relay = new(1, 1);
        int input = PlanetaryResources.AqueousLiquids;
        colony = industry
            .Update(
                f.PilotId,
                PlanetId,
                [
                    new RemovePlanetaryLink(new(port), new(producer)),
                    new CreatePlanetaryPin(relay, Processor, 1, 1.0025),
                    new SetPlanetarySchematic(relay, WaterSchematic),
                    new CreatePlanetaryLink(new(port), relay, 0),
                    new CreatePlanetaryLink(relay, new(producer), 0),
                    new CreatePlanetaryRoute(new(1, 2), [new(port), relay, new(producer)], input, 3000),
                    new CreatePlanetaryRoute(new(2, 2), [new(producer), relay, new(port)], Water, 20),
                    new CreatePlanetaryRoute(new(3, 2), [new(port), relay], input, 3000),
                    new CreatePlanetaryRoute(new(4, 2), [relay, new(port)], Water, 20),
                ]
            )
            .Colony;
        long middle = colony.Pins.Single(p => p.TypeId == Processor && p.Id != producer).Id;
        var transit = colony.Routes.Where(r => r.Path.Count == 3 && r.Path[1] == middle).Select(r => r.Id).ToHashSet();
        Assert.Equal(2, transit.Count);
        var replacement = Catalog(f)
            .Schematics.Values.OrderBy(recipe => recipe.Id)
            .First(recipe => recipe.Id != WaterSchematic && recipe.PinTypes.Contains(Processor));

        var edited = industry.Update(f.PilotId, PlanetId, [new SetPlanetarySchematic(new(middle), replacement.Id)]).Colony;

        Assert.Equal(transit.Order(), edited.Routes.Where(r => r.Path.Contains(middle)).Select(r => r.Id).Order());
        Assert.Equal(replacement.Id, edited.Pins.Single(p => p.Id == middle).SchematicId);
        f.Clock.Seconds += 3600;
        var finished = industry.Read(f.PilotId, PlanetId).Colony!;
        Assert.True(PlanetarySimulation.Quantity(finished.Pins.Single(p => p.Id == port), Water) > 0);
        await live.FlushAsync();
        live.Dispose();
        using WorldDbContext saved = new(f.Db.Path);
        var restored = PilotPlanetaryIndustryStore.ReadState(saved.Set<SavedPlanetaryColony>().Find(f.PilotId, PlanetId)!);
        Assert.Equal(transit.Order(), restored.Routes.Where(r => r.Path.Contains(middle)).Select(r => r.Id).Order());
    }

    [Fact]
    public void ExtractsProcessesAndPersistsWithoutReplayingCycles()
    {
        using var f = Prepare();
        var initial = Deploy(f);
        var industry = Industry(f);
        var ecu = Assert.Single(initial.Pins, p => p.TypeId == Extractor);
        Assert.Equal(4, initial.Level);
        Assert.Equal(4, initial.Pins.Count);
        Assert.Equal(4, ecu.Program!.ExpiresAt / ecu.Program.CycleTicks - ecu.Program.InstalledAt / ecu.Program.CycleTicks);
        Assert.Empty(initial.Pins.Single(p => p.TypeId == Launchpad).Contents);
        f.Clock.Seconds += 3600;
        var colony = industry.Read(f.PilotId, PlanetId).Colony!;
        var port = colony.Pins.Single(p => p.TypeId == Launchpad);
        Assert.True(PlanetarySimulation.Quantity(port, Water) >= 20);
        Assert.True(PlanetarySimulation.Quantity(port, PlanetaryResources.AqueousLiquids) >= 0);
        Assert.Null(colony.Pins.Single(p => p.Id == ecu.Id).NextRunAt);
        var savedJson = JsonSerializer.Serialize(colony);
        var reopened = new PilotPlanetaryIndustryStore(new(f.Db.Path), f.Clock, f.Db.Services.GetRequiredService<PilotTrainingStore>());
        Assert.Equal(savedJson, JsonSerializer.Serialize(reopened.Read(f.PilotId, PlanetId).Colony));
        Assert.Equal(savedJson, JsonSerializer.Serialize(industry.Read(f.PilotId, PlanetId).Colony));
        Assert.Empty(initial.Pins.Single(p => p.TypeId == Launchpad).Contents);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(savedJson, world.Set<SavedPlanetaryColony>().Find(f.PilotId, PlanetId)!.StateJson);
    }

    [Fact]
    public void EveryPinnedSchematicConsumesExactInputsAndProducesItsCatalogOutput()
    {
        using var f = Prepare();
        var catalog = Catalog(f);
        Assert.Equal(68, catalog.Schematics.Count);
        Assert.Equal(15, catalog.CommodityTiers.Count(p => p.Value == 0));
        Assert.All(catalog.CommodityTiers.Values, tier => Assert.InRange(tier, 0, 4));
        foreach (var recipe in catalog.Schematics.Values)
        {
            int type = recipe.PinTypes.First();
            long start = f.Clock.Now;
            PlanetaryPin store = new(1, Storage, 1, 1, start, recipe.Inputs.ToArray());
            PlanetaryPin processor = new(2, type, 1, 1.001, start, [], recipe.Id);
            var routes = recipe
                .Inputs.Select((input, index) => new PlanetaryRoute(index + 1, [1, 2], input.TypeId, input.Quantity))
                .Append(new(recipe.Inputs.Count + 1, [2, 1], recipe.Output.TypeId, recipe.Output.Quantity))
                .ToArray();
            PlanetaryColony colony = new(f.PilotId, PlanetId, 5, start, 10, 10, [store, processor], [new(1, 2, 0)], routes);
            var running = PlanetarySimulation.Advance(colony, catalog, start);
            Assert.Empty(running.Pins.Single(p => p.Id == 1).Contents);
            Assert.Equal(start + recipe.CycleSeconds * TimeSpan.TicksPerSecond, running.Pins.Single(p => p.Id == 2).NextRunAt);
            long completion = start + recipe.CycleSeconds * TimeSpan.TicksPerSecond;
            var finished = PlanetarySimulation.Advance(running, catalog, completion);
            Assert.Equal(recipe.Output, Assert.Single(finished.Pins.Single(p => p.Id == 1).Contents));
            Assert.Null(finished.Pins.Single(p => p.Id == 2).NextRunAt);
            Assert.Equal(
                JsonSerializer.Serialize(finished),
                JsonSerializer.Serialize(PlanetarySimulation.Advance(finished, catalog, completion))
            );
            Assert.Equal(recipe.Inputs, store.Contents);
        }
    }

    [Fact]
    public void RejectedBatchPreservesWalletCargoColonyAndIdentitySequence()
    {
        using var f = Prepare();
        long center = f.Grant(CommandCenter, cargo: true);
        var industry = Industry(f);
        using WorldDbContext before = new(f.Db.Path);
        long balance = before.Set<PilotWallet>().Find(f.PilotId)!.Cents;
        long next = before.Set<SavedPlanetaryIdentity>().Find(1)!.NextPinId;
        Assert.Throws<InvalidDataException>(() =>
            industry.Update(
                f.PilotId,
                PlanetId,
                [new CreatePlanetaryPin(new(center), CommandCenter, 1, 1), new CreatePlanetaryPin(new(1, 1), Launchpad, 1, 1.001)]
            )
        );
        using WorldDbContext after = new(f.Db.Path);
        Assert.Equal(balance, after.Set<PilotWallet>().Find(f.PilotId)!.Cents);
        Assert.Equal(next, after.Set<SavedPlanetaryIdentity>().Find(1)!.NextPinId);
        Assert.Equal(1, after.Set<OwnedItem>().Find(center)!.Quantity);
        Assert.Empty(after.Set<SavedPlanetaryColony>());
        Assert.Empty(after.Set<SavedPlanetaryOffice>());
        Assert.Empty(after.Set<SavedServicePayment>());
    }

    [Fact]
    public void RetryDoesNotChargeOrConsumeTheCommandCenterTwice()
    {
        using var f = Prepare();
        long center = f.Grant(CommandCenter, 2, cargo: true);
        PlanetaryCommand[] commands =
        [
            new CreatePlanetaryPin(new(center), CommandCenter, 1, 1),
            new UpgradePlanetaryCommand(new(center), 1),
        ];
        var industry = Industry(f);
        var first = industry.Update(f.PilotId, PlanetId, commands);
        var again = industry.Update(f.PilotId, PlanetId, commands);
        Assert.Equal(JsonSerializer.Serialize(first.Colony), JsonSerializer.Serialize(again.Colony));
        Assert.Empty(again.Items);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(1, world.Set<OwnedItem>().Find(center)!.Quantity);
        Assert.Equal(1, world.Set<SavedServicePayment>().Count(p => p.Kind == WalletEntryType.PlanetaryConstruction));
        Assert.Equal(first.BalanceCents, world.Set<PilotWallet>().Find(f.PilotId)!.Cents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomsImportExportAndShipCollectionPreserveRealCommoditiesAndTaxes(bool resident)
    {
        using var f = Prepare();
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        var colony = Deploy(f);
        var industry = Industry(f);
        long office = PlanetaryRules.OfficeId(PlanetId);
        var contact = Assert.Single(industry.OrbitContacts(f.Db.Store.FindPilot(1000001)!.SystemId, f.Clock.Now));
        Assert.Equal(PlanetaryRules.CustomsOfficeType, contact.TypeId);
        f.Db.Store.SetPosition(f.ShipId, contact.Position);
        long cargo = f.Grant(PlanetaryResources.AqueousLiquids, 6000, cargo: true);
        var moved = industry.MoveOrbitCargo(f.PilotId, office, true, new([cargo], f.ShipId, (int)InventoryFlag.Hangar, null));
        Assert.Equal(2, moved.Count);
        var importedStack = Assert.Single(industry.OrbitInventory(f.PilotId, office).Items);
        long port = colony.Pins.Single(p => p.TypeId == Launchpad).Id;
        var imported = industry.ImportExport(f.PilotId, office, port, [new(importedStack.Id, 6000)], []);
        Assert.Empty(industry.OrbitInventory(f.PilotId, office).Items);
        Assert.Equal(0, PlanetarySimulation.Quantity(imported.Colony.Pins.Single(p => p.Id == port), PlanetaryResources.AqueousLiquids));
        Assert.Equal(JsonSerializer.Serialize(imported.Colony), JsonSerializer.Serialize(industry.Read(f.PilotId, PlanetId).Colony));
        f.Clock.Seconds += 1800;
        var produced = industry.Read(f.PilotId, PlanetId).Colony!;
        Assert.Equal(20, PlanetarySimulation.Quantity(produced.Pins.Single(p => p.Id == port), Water));
        var exported = industry.ImportExport(f.PilotId, office, port, [], [new(Water, 20)]);
        Assert.Equal(0, PlanetarySimulation.Quantity(exported.Colony.Pins.Single(p => p.Id == port), Water));
        var water = Assert.Single(industry.OrbitInventory(f.PilotId, office).Items);
        Assert.Equal(Water, water.TypeId);
        var changes = f.Db.Services.GetRequiredService<IWrecks>().Take(f.PilotId, f.ShipId, office, [water.Id], 20, f.Clock.Now);
        Assert.Equal(f.ShipId, Assert.Single(changes).Item.LocationId);
        Assert.Empty(industry.OrbitInventory(f.PilotId, office).Items);
        live?.Dispose();
        using WorldDbContext world = new(f.Db.Path);
        var import = Assert.Single(world.Set<SavedServicePayment>(), p => p.Kind == WalletEntryType.PlanetaryImportTax);
        Assert.Equal(-255_000, import.AmountCents);
        var export = Assert.Single(world.Set<SavedServicePayment>(), p => p.Kind == WalletEntryType.PlanetaryExportTax);
        Assert.Equal(-136_000, export.AmountCents);
    }

    [Fact]
    public void OverdrawAndRemoteCustomsRequestsLeaveGoodsAndWalletUnchanged()
    {
        using var f = Prepare();
        var colony = Deploy(f);
        var industry = Industry(f);
        long office = PlanetaryRules.OfficeId(PlanetId);
        long port = colony.Pins.Single(p => p.TypeId == Launchpad).Id;
        var contact = Assert.Single(industry.OrbitContacts(f.Db.Store.FindPilot(1000001)!.SystemId, f.Clock.Now));
        const int OtherSystem = 30000144;
        f.Db.Store.SetPosition(f.ShipId, contact.Position, OtherSystem);
        Assert.Contains(
            "in its solar system",
            Assert.Throws<InvalidDataException>(() => industry.ImportExport(f.PilotId, office, port, [], [new(Water, 1)])).Message
        );
        f.Db.Store.SetPosition(f.ShipId, contact.Position, contact.SystemId);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotWallet>().Find(f.PilotId)!.Cents = 0;
            world.SaveChanges();
        }
        long cargo = f.Grant(Water, 10, cargo: true);
        industry.MoveOrbitCargo(f.PilotId, office, true, new([cargo], f.ShipId, (int)InventoryFlag.Hangar, null));
        var stack = Assert.Single(industry.OrbitInventory(f.PilotId, office).Items);
        Assert.Throws<InvalidDataException>(() => industry.ImportExport(f.PilotId, office, port, [new(stack.Id, 10)], []));
        Assert.Equal(10, Assert.Single(industry.OrbitInventory(f.PilotId, office).Items).Quantity);
        Assert.Equal(JsonSerializer.Serialize(colony), JsonSerializer.Serialize(industry.Read(f.PilotId, PlanetId).Colony));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandLaunchCanBeCollectedOnceAndCannotBeOpenedByAnotherPilotOrAfterExpiry(bool resident)
    {
        using var f = Prepare();
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        var colony = Deploy(f);
        var industry = Industry(f);
        f.Clock.Seconds += 3600;
        colony = industry.Read(f.PilotId, PlanetId).Colony!;
        long port = colony.Pins.Single(p => p.TypeId == Launchpad).Id;
        long center = colony.Pins.Single(p => p.TypeId == CommandCenter).Id;
        industry.Update(f.PilotId, PlanetId, [new CreatePlanetaryLink(new(port), new(center), 0)]);
        var transfer = industry.Transfer(f.PilotId, PlanetId, [port, center], [new(Water, 20)]);
        Assert.Equal(20, PlanetarySimulation.Quantity(transfer.Colony.Pins.Single(p => p.Id == center), Water));
        var launched = industry.Launch(f.PilotId, PlanetId, center, [new(Water, 20)]);
        var launch = Assert.Single(industry.Launches(f.PilotId));
        Assert.Equal(0, PlanetarySimulation.Quantity(launched.Colony.Pins.Single(p => p.Id == center), Water));
        Assert.Equal(f.Clock.Now + PlanetaryRules.LaunchLifetime, launch.ExpiresAt);
        Assert.Throws<InvalidDataException>(() => industry.Launch(f.PilotId, PlanetId, center, [new(Water, 20)]));
        f.Db.Store.SetPosition(f.ShipId, launch.Position);
        var item = Assert.Single(industry.OrbitInventory(f.PilotId, launch.Id).Items);
        Assert.Throws<InvalidDataException>(() => industry.OrbitInventory(f.PilotId + 1, launch.Id));
        var changes = f.Db.Services.GetRequiredService<IWrecks>().Take(f.PilotId, f.ShipId, launch.Id, [item.Id], null, f.Clock.Now);
        Assert.Equal(20, Assert.Single(changes).Item.Quantity);
        Assert.Empty(industry.OrbitInventory(f.PilotId, launch.Id).Items);
        Assert.Throws<InvalidDataException>(() =>
            f.Db.Services.GetRequiredService<IWrecks>().Take(f.PilotId, f.ShipId, launch.Id, [item.Id], null, f.Clock.Now)
        );
        live?.Dispose();
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(launch, Assert.Single(industry.Launches(f.PilotId)));
        f.Clock.Seconds += PlanetaryRules.LaunchLifetime / TimeSpan.TicksPerSecond;
        Assert.Empty(industry.Launches(f.PilotId));
        Assert.Throws<InvalidDataException>(() => industry.OrbitInventory(f.PilotId, launch.Id));
    }

    [Fact]
    public async Task ResidentContactsInvalidateOnCommitAndCheckpointRetainsColonyProgress()
    {
        using var f = Prepare();
        var database = new WorldDatabase(f.Db.Path);
        using (LiveWorld live = new(database, f.Clock, _ => { }))
        {
            WreckStore wrecks = new(database);
            int system = f.Db.Store.FindPilot(1000001)!.SystemId;
            Assert.Empty(wrecks.List(system, f.Clock.Now));
            using (var empty = SessionPerformance.Begin("empty planetary orbit", _ => { }, force: true)!)
            {
                Assert.Empty(wrecks.List(system, f.Clock.Now));
                Assert.Equal(0, empty.Commands);
                Assert.Equal(0, empty.Connections);
                Assert.Equal(0, empty.FittingCalculations);
            }
            Deploy(f);
            WreckContact office;
            List<string> queries = [];
            using (var refresh = SessionPerformance.Begin("refresh planetary orbit", _ => { }, force: true, command: queries.Add)!)
            {
                office = Assert.Single(wrecks.List(system, f.Clock.Now));
                Assert.DoesNotContain(queries, sql => sql.Contains("World", StringComparison.Ordinal));
                Assert.Equal(0, refresh.FittingCalculations);
            }
            Assert.Equal(PlanetaryRules.OfficeId(PlanetId), office.Id);
            using (var warmed = SessionPerformance.Begin("warmed planetary orbit", _ => { }, force: true)!)
            {
                Assert.Equal(office, Assert.Single(wrecks.List(system, f.Clock.Now)));
                Assert.Equal(0, warmed.Commands);
                Assert.Equal(0, warmed.Connections);
                Assert.Equal(0, warmed.FittingCalculations);
            }
            f.Clock.Seconds += 3600;
            var progress = Industry(f).Read(f.PilotId, PlanetId).Colony!;
            await live.FlushAsync();
            Assert.True(PlanetarySimulation.Quantity(progress.Pins.Single(p => p.TypeId == Launchpad), Water) > 0);
        }
        WorldInitialization.Initialize(database, f.Clock);
        var reopened = Industry(f).Read(f.PilotId, PlanetId).Colony!;
        Assert.True(PlanetarySimulation.Quantity(reopened.Pins.Single(p => p.TypeId == Launchpad), Water) > 0);
    }

    [Fact]
    public void ColonyAndUpgradeSkillsAreAuthoritativeAndBasicDeploymentNeedsNoScanningSkill()
    {
        using var f = Prepare();
        f.Train(PlanetaryRules.RemoteSensingSkill, 0);
        f.Train(PlanetaryRules.CommandCenterUpgradesSkill, 0);
        long center = f.Grant(CommandCenter, cargo: true);
        var industry = Industry(f);
        var colony = industry.Update(f.PilotId, PlanetId, [new CreatePlanetaryPin(new(center), CommandCenter, 1, 1)]).Colony;
        Assert.Equal(0, colony.Level);
        Assert.Throws<InvalidDataException>(() =>
            industry.Update(f.PilotId, PlanetId, [new UpgradePlanetaryCommand(new(colony.Pins[0].Id), 1)])
        );
        Assert.Throws<InvalidDataException>(() => industry.ResourceField(f.PilotId, PlanetId, PlanetaryResources.AqueousLiquids, 30));
        using (WorldDbContext world = new(f.Db.Path))
        {
            long account = world.Pilots.Single(p => p.Id == f.PilotId).AccountId;
            world.Set<Account>().Find(account)!.CloneGrade = AccountCloneGrade.Alpha;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => industry.Update(f.PilotId, PlanetId, [new CreatePlanetaryPin(new(1, 1), Storage, 1, 1)]));
        industry.Abandon(f.PilotId, PlanetId);
        Assert.Empty(industry.List(f.PilotId));
    }

    [Fact]
    public void PreviousSchemaUpgradesIdempotentlyWithoutChangingExistingPilots()
    {
        using var f = Prepare();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldPlanetaryColony;
                DROP TABLE WorldPlanetaryIdentity;
                DROP TABLE WorldPlanetaryLaunch;
                DROP TABLE WorldPlanetaryOffice;
                PRAGMA user_version=138;
                """
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext upgraded = new(f.Db.Path);
        Assert.NotNull(upgraded.Pilots.Find(f.PilotId));
        Assert.Equal(18_000_000_000_000, upgraded.Set<SavedPlanetaryIdentity>().Find(1)!.NextPinId);
        Assert.Empty(upgraded.Set<SavedPlanetaryColony>());
        Assert.Empty(upgraded.Database.SqlQueryRaw<long>("SELECT rowid AS Value FROM pragma_foreign_key_check"));
    }
}
