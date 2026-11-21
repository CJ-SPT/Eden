using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Protocol.PilotSessions;
using Eden.Server;
using Eden.Server.Launcher;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    private const string FreshCatalog = "EDEN_FRESH_UNIVERSE_CATALOG";
    private const int JitaHub = 60003760;
    private const int SobasekiLab = 60002419;
    private const int Charon = 20185;
    private const int CharonOriginal = 20186;
    private const int RheaCopy = 28845;
    private const int ExpandedCargohold = 1319;
    private const int CargoRig = 31117;

    private sealed class InstalledCapitalMarketFactAttribute : FactAttribute
    {
        public InstalledCapitalMarketFactAttribute()
        {
            string? path = Environment.GetEnvironmentVariable(FreshCatalog);
            if (!File.Exists(path) || !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "market-orders", "manifest.json")))
            {
                Skip = $"Set {FreshCatalog} to build 3662168 catalog.sqlite next to a captured market-orders snapshot.";
            }
        }
    }

    [InstalledCapitalMarketFact]
    public async Task FreshStandardMarketFundsBuildsAndDeliversTheSameRhea()
    {
        string source = Environment.GetEnvironmentVariable(FreshCatalog)!;
        var directory = TestDirectory.Create("eden-fresh-capital-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            File.Copy(source, database.OriginalCatalogPath);
            string marketSource = Path.Combine(Path.GetDirectoryName(source)!, "market-orders");
            var manifest = database.MarketSnapshot.ReadManifest(marketSource);
            string marketCopy = database.MarketSnapshot.Root;
            Directory.CreateDirectory(Path.Combine(marketCopy, manifest.Generation));
            File.Copy(Path.Combine(marketSource, "manifest.json"), Path.Combine(marketCopy, "manifest.json"));
            foreach (var region in manifest.Regions)
            {
                string file = Path.Combine(manifest.Generation, $"{region.RegionId}.json.gz");
                File.Copy(Path.Combine(marketSource, file), Path.Combine(marketCopy, file));
            }
            TestClock clock = new(manifest.CapturedAt);
            WorldInitialization.Initialize(database, clock, new(FreshUniverse: true));
            var configuration = new WorldConfigurationStore(database, clock);
            var current = configuration.Read();
            configuration.Save(
                current.Generation,
                current.Active.Draft with
                {
                    Settings = current.Active.Draft.Settings with
                    {
                        Economy = current.Active.Draft.Settings.Economy with { StandardMarket = true, ProgressiveMarkets = false },
                        Rewards = new(BountyMultiplier: WorldTuningRules.MaximumRate),
                    },
                },
                "capital acquisition test"
            );
            WorldInitialization.Initialize(database, clock);
            long freighterPilot;
            long freighterShip;
            JumpTimers timers;
            using (var host = ServerServices.Build(database, clock, population: new TestNpcPopulationSeeder(), liveWorld: true))
            {
                Assert.True(database.Settings.FreshUniverse);
                Assert.True(database.Settings.Economy.StandardMarket);
                Assert.False(database.Settings.DevelopmentSupply.TestingMarket);
                Assert.False(database.Settings.DevelopmentSupply.TestingGrants);
                var live = host.GetRequiredService<LiveWorld>();
                Assert.Empty(live.Economy.Inspect(world => world.Pilots.ToArray()));
                var store = host.GetRequiredService<SqliteWorld>();
                var market = host.GetRequiredService<IPilotMarket>();
                var industry = host.GetRequiredService<IPilotIndustry>();
                var inventory = host.GetRequiredService<IPilotInventory>();
                var fitting = host.GetRequiredService<IPilotFitting>();
                var assembly = host.GetRequiredService<IPilotFleet>();
                var travel = host.GetRequiredService<IPilotCapitalTravel>();
                using CatalogDbContext catalog = new(database.CatalogPath);
                var builder = CreatePilot(host, "Capital Builder");
                var scout = CreatePilot(host, "Capital Scout");
                freighterPilot = builder.Pilot.PilotId;
                var original = catalog.Require(CatalogData.Client.Blueprints, CharonOriginal.ToString());
                var copying = original.Activities!.Copying!;
                var invention = original.Activities.Invention!;
                var recipes = ManufacturingCatalog.Recipes(catalog);
                var charonRecipe = recipes[CharonOriginal];
                var rheaRecipe = recipes[RheaCopy];

                // Advance the two pilots' training in the fixture. Their wallets use normal bounty settlement.
                TrainForCapitalTest(
                    live,
                    fitting,
                    builder.Pilot,
                    [Rhea],
                    [
                        .. charonRecipe.Skills,
                        .. rheaRecipe.Skills,
                        .. (invention.Skills ?? []).Select(skill => new ManufacturingRequiredSkill(
                            (int)skill.TypeID!.Value,
                            (int)skill.Level!.Value
                        )),
                        new(CapitalTravelRules.JumpDriveOperation, 5),
                        new(CapitalTravelRules.JumpDriveCalibration, 1),
                    ]
                );
                TrainForCapitalTest(
                    live,
                    fitting,
                    scout.Pilot,
                    [(int)StarterShipType.Venture, CapitalTravelRules.IndustrialGenerator, ExpandedCargohold, CargoRig],
                    [new(CapitalTravelRules.CynosuralFieldTheory, 5)]
                );
                FundFromBounties(builder.Pilot, 4);
                FundFromBounties(scout.Pilot, 1);
                var atJita = PlaceAtStation(builder.Pilot, JitaHub);
                var scoutAtJita = PlaceAtStation(scout.Pilot, JitaHub);
                long purchasedOriginal = Buy(freighterPilot, CharonOriginal, 1).Id;
                Assert.Equal(-1, industry.Blueprint(freighterPilot, purchasedOriginal).RunsRemaining);
                Assert.DoesNotContain(market.Orders(freighterPilot, RheaCopy), order => !order.Bid);
                foreach (var material in charonRecipe.Materials.Concat(rheaRecipe.Materials).Where(m => m.TypeId != Charon))
                {
                    Buy(freighterPilot, (int)material.TypeId!.Value, (int)material.Quantity!.Value);
                }
                var cores = (invention.Materials ?? [])
                    .Select(material => Buy(freighterPilot, (int)material.TypeID!.Value, (int)material.Quantity!.Value).Id)
                    .ToArray();
                var isotope = Buy(freighterPilot, Nitrogen, 60_000);
                var shipment = Buy(freighterPilot, Cargo, 14);
                long venture = AssembleAndBoard(scoutAtJita, Buy(scout.Pilot.PilotId, (int)StarterShipType.Venture, 1).Id);
                long generator = Buy(scout.Pilot.PilotId, CapitalTravelRules.IndustrialGenerator, 1).Id;
                long expander = Buy(scout.Pilot.PilotId, ExpandedCargohold, 1).Id;
                long rigs = Buy(scout.Pilot.PilotId, CargoRig, 3).Id;
                foreach (long item in new[] { generator, expander })
                {
                    fitting.Fit(scout.Pilot.PilotId, [item], JitaHub, (int)InventoryFlag.AutoFit);
                    fitting.SetOnline(scout.Pilot.PilotId, venture, item, true);
                }
                for (int rig = 0; rig < 3; rig++)
                {
                    fitting.Fit(scout.Pilot.PilotId, [rigs], JitaHub, (int)InventoryFlag.AutoFit);
                }
                var ozone = Buy(scout.Pilot.PilotId, LiquidOzone, 200);
                inventory.Move(scout.Pilot.PilotId, [ozone.Id], JitaHub, venture, (int)InventoryFlag.Cargo, null);

                Assert.True(industry.Facility(freighterPilot, SobasekiLab).Laboratory);
                Carry(atJita, [purchasedOriginal, .. cores], JitaHub, SobasekiLab);
                var copy = Science(purchasedOriginal, CharonOriginal, IndustryActivity.Copying, copying.Time!.Value, [], 1);
                var rheaPrint = Science(
                    copy.Id,
                    RheaCopy,
                    IndustryActivity.Invention,
                    invention.Time!.Value,
                    (invention.Materials ?? []).Select(material => new ManufacturingMaterial(material.TypeID, material.Quantity)).ToArray()
                );
                Assert.Equal(1, rheaPrint.BlueprintRuns);
                Assert.Equal(IndustryDefinitions.InventionMaterialEfficiency, rheaPrint.MaterialEfficiency);
                Assert.DoesNotContain(inventory.List(freighterPilot, SobasekiLab), item => item.Id == copy.Id);
                Carry(atJita, [purchasedOriginal, rheaPrint.Id], SobasekiLab, JitaHub);
                Assert.Equal(Charon, Manufacture(purchasedOriginal, charonRecipe).TypeId);
                var package = Manufacture(rheaPrint.Id, rheaRecipe);
                Assert.DoesNotContain(inventory.List(freighterPilot, JitaHub), item => item.TypeId == Charon || item.Id == rheaPrint.Id);
                freighterShip = AssembleAndBoard(atJita, package.Id);
                inventory.Move(freighterPilot, [isotope.Id], JitaHub, freighterShip, (int)InventoryFlag.FuelBay, null);
                var loaded = inventory.Move(freighterPilot, [shipment.Id], JitaHub, freighterShip, (int)InventoryFlag.Cargo, null);
                long cargoItem = Assert.Single(loaded, change => change.Item.LocationId == freighterShip).Item.Id;

                store.SetPosition(freighterShip, new(10_000_000, 0, 0), Jita);
                store.SetPosition(venture, s_station + new SpacePosition(-40_213, 0, 0), Jan);
                using (Client freighter = new(host, clock, builder.Account, store.FindPilot(builder.Account)!))
                using (Client emitter = new(host, clock, scout.Account, store.FindPilot(scout.Account)!))
                {
                    var fleets = host.GetRequiredService<ICooperativeFleets>();
                    long fleet = fleets.Create(emitter.Pilot.PilotId).Id;
                    fleets.Invite(
                        emitter.Pilot.PilotId,
                        fleet,
                        freighterPilot,
                        new(FleetRules.NoGroup, FleetRules.NoGroup, FleetRole.Member)
                    );
                    fleets.Accept(freighterPilot, fleet);
                    var ignited = emitter.Call("dogmaIM", "Activate", generator, "cynosuralGeneration", null, 0);
                    Assert.True(ignited is PilotCallResult.Success, ignited.ToString());
                    var beacon = Assert.IsType<CynosuralField>(travel.Field(emitter.Pilot.PilotId));
                    Assert.DoesNotContain(inventory.List(emitter.Pilot.PilotId, venture), item => item.TypeId == LiquidOzone);
                    var jump = freighter.Call("beyonce", "CmdJumpToFleetModuleBeacon", beacon.PilotId, beacon.Beacon.Id, beacon.SystemId);
                    Assert.True(jump is PilotCallResult.Success, jump.ToString());
                    await freighter.TickAsync();
                    clock.Seconds += 3;
                    await freighter.TickAsync();
                    Assert.Equal((Jan, freighterShip), (freighter.Pilot.SystemId, freighter.Pilot.ShipId));
                    Assert.Equal(
                        12_587,
                        Assert.Single(inventory.List(freighterPilot, freighterShip), item => item.TypeId == Nitrogen).Quantity
                    );
                    timers = Assert.IsType<JumpTimers>(travel.Timers(freighterPilot));
                    var docked = freighter.Context.Flight.Execute(new FlightCommand.Dock(JanStation, freighterShip));
                    Assert.True(docked is PilotCallResult.Success, docked.ToString());
                    inventory.Move(freighterPilot, [cargoItem], freighterShip, JanStation, (int)InventoryFlag.Hangar, null);
                }
                Assert.Empty(live.Economy.Inspect(world => world.Set<TestingWalletGrant>().ToArray()));
                Assert.DoesNotContain(market.Transactions(freighterPilot), transaction => transaction.TypeId is Charon or Rhea or RheaCopy);
                await live.FlushAsync();

                void FundFromBounties(DockedPilot pilot, int kills)
                {
                    const int officer = 13603;
                    using var world = live.Economy.Begin(new(pilots: [pilot.PilotId], systems: [Jan]));
                    new TestNpcPopulationSeeder(Jan, officer).Seed(world, catalog, Jan, clock.Now);
                    world.SaveChanges();
                    var owner = world.Pilots.Find(pilot.PilotId)!;
                    var npcs = world
                        .Filter<WorldNpc>(npc => npc.SystemId == Jan && npc.TypeId == officer && npc.DestroyedAt == null)
                        .OrderBy(npc => npc.Id)
                        .Take(kills)
                        .ToArray();
                    Assert.Equal(kills, npcs.Length);
                    foreach (var npc in npcs)
                    {
                        var hit = NpcDamagePersistence.Apply(
                            world,
                            catalog,
                            npc,
                            owner,
                            pilot.ShipId,
                            catalog.FittingAttributes(officer),
                            new(1_000_000, 0, 0, 0),
                            clock.Now
                        );
                        Assert.True(hit.Destroyed);
                        Assert.NotNull(hit.Reward);
                    }
                    world.Commit();
                }

                InventoryItem Buy(long pilot, int type, int count)
                {
                    var ask = market
                        .Orders(pilot, type)
                        .Where(order =>
                            !order.Bid && order.StationId == JitaHub && order.Remaining >= count && order.MinimumVolume <= count
                        )
                        .MinBy(order => order.PriceCents);
                    Assert.True(ask is not null, $"Missing Jita ask for {count} of type {type}.");
                    var purchase = market.Buy(pilot, JitaHub, [new(type, ask.PriceCents, count)]);
                    Assert.Equal(count, purchase.Trades.Sum(trade => trade.Quantity));
                    return Assert.Single(inventory.List(pilot, JitaHub), item => item.TypeId == type);
                }

                DockedPilot PlaceAtStation(DockedPilot pilot, int station)
                {
                    var location = catalog.RequireStation(station);
                    store.SetPosition(pilot.ShipId, new(location.X, location.Y, location.Z), location.SolarSystemId);
                    return host.GetRequiredService<IStationDocking>().Dock(pilot.PilotId, pilot.ShipId, station);
                }

                void Carry(DockedPilot pilot, IReadOnlyList<long> items, int from, int to)
                {
                    inventory.Move(pilot.PilotId, items, from, pilot.ShipId, (int)InventoryFlag.Cargo, null);
                    PlaceAtStation(pilot, to);
                    inventory.Move(pilot.PilotId, items, pilot.ShipId, to, (int)InventoryFlag.Hangar, null);
                }

                long AssembleAndBoard(DockedPilot pilot, long item)
                {
                    long ship = Assert.Single(assembly.Assemble(pilot.PilotId, [item], null).ShipIds);
                    return assembly.Board(pilot.PilotId, ship, pilot.ShipId).Pilot.ShipId;
                }

                InventoryItem Science(
                    long input,
                    int product,
                    IndustryActivity activity,
                    long seconds,
                    IReadOnlyList<ManufacturingMaterial> materials,
                    int runs = 0
                )
                {
                    var owned = industry.Blueprint(freighterPilot, input);
                    var request = new IndustryRequest(
                        input,
                        owned.Item.TypeId,
                        SobasekiLab,
                        1,
                        product,
                        0,
                        IndustryMaterials.Calculate(materials, 1),
                        Activity: activity,
                        LicensedRuns: runs
                    );
                    var plan = ScienceRules.Plan(
                        request,
                        new(
                            new(owned.MaterialEfficiency, owned.TimeEfficiency, owned.RunsRemaining),
                            seconds,
                            (int)original.MaxProductionLimit!.Value,
                            0,
                            industry.CharacterBonuses(freighterPilot),
                            materials
                        ),
                        checkQuote: false
                    );
                    var science = new PilotIndustryStore(database, clock, new SuccessfulCapitalInvention());
                    return Deliver(science, science.Install(freighterPilot, request with { QuotedSeconds = plan.DurationSeconds }).Job);
                }

                InventoryItem Manufacture(long input, ManufacturingRecipeDefinition recipe)
                {
                    var owned = industry.Blueprint(freighterPilot, input);
                    var levels = host.GetRequiredService<IPilotSkillReader>()
                        .ReadSkills(freighterPilot)
                        .ToDictionary(skill => skill.TypeId);
                    var bonuses = recipe
                        .Skills.Select(skill => new ManufacturingSkillTimeBonus(
                            fitting.Attributes(skill.TypeId).GetValueOrDefault(DogmaAttributeId.ManufactureTimePerLevel),
                            levels[skill.TypeId].Level
                        ))
                        .ToArray();
                    var plan = ManufacturingRules.Plan(
                        1,
                        new(
                            recipe.Seconds,
                            0,
                            industry.CharacterBonuses(freighterPilot),
                            bonuses,
                            recipe.Materials,
                            new(owned.MaterialEfficiency, owned.TimeEfficiency, owned.RunsRemaining)
                        )
                    );
                    return Deliver(
                        industry,
                        industry
                            .Install(
                                freighterPilot,
                                new(input, recipe.BlueprintTypeId, JitaHub, 1, recipe.ProductTypeId, plan.DurationSeconds, plan.Materials)
                            )
                            .Job
                    );
                }

                InventoryItem Deliver(IPilotIndustry service, IndustryJobDetails job)
                {
                    Assert.True(job.CostCents > 0);
                    Assert.Throws<InvalidDataException>(() => service.Deliver(freighterPilot, job.Id, job.Facility.SystemId));
                    clock.AdvanceWallTime(TimeSpan.FromSeconds(job.DurationSeconds));
                    var result = service.Deliver(freighterPilot, job.Id, job.Facility.SystemId);
                    Assert.Empty(service.Deliver(freighterPilot, job.Id, job.Facility.SystemId).Changes);
                    return Assert
                        .Single(
                            result.Changes,
                            change =>
                                change.Item.TypeId == job.ProductTypeId && change.Item.Id != job.BlueprintId && change.Item.Quantity > 0
                        )
                        .Item;
                }
            }

            WorldInitialization.Initialize(database, clock);
            using var reopened = ServerServices.Build(database, clock, liveWorld: true);
            var result = reopened
                .GetRequiredService<IPilotLookup>()
                .ListPilots(
                    reopened.GetRequiredService<LiveWorld>().Economy.Inspect(world => world.Pilots.Find(freighterPilot)!.AccountId)
                );
            Assert.Equal((Rhea, freighterShip, JanStation), (Assert.Single(result).ShipTypeId, result[0].ShipId, result[0].StationId));
            Assert.Equal(timers, reopened.GetRequiredService<IPilotCapitalTravel>().Timers(freighterPilot));
            Assert.Contains(
                reopened.GetRequiredService<IPilotInventory>().List(freighterPilot, JanStation),
                item => item.TypeId == Cargo && item.Quantity == 14
            );
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static (long Account, DockedPilot Pilot) CreatePilot(IServiceProvider host, string name)
    {
        long account = host.GetRequiredService<LauncherStore>().Register(new(name, "Capital Test Password 1")).AccountId;
        long id = host.GetRequiredService<ICharacterCreation>()
            .Create(account, new(name, 1, 1, 0, CharacterRules.UnspecifiedAncestry, [], [], 18));
        var pilot = Assert.Single(host.GetRequiredService<IPilotLookup>().ListPilots(account), pilot => pilot.PilotId == id);
        Assert.Equal(0, host.GetRequiredService<IPilotMarket>().Balance(id));
        Assert.Equal((int)HullClass.Corvette, pilot.ShipGroupId);
        return (account, pilot);
    }

    private static void TrainForCapitalTest(
        LiveWorld live,
        IPilotFitting fitting,
        DockedPilot pilot,
        IReadOnlyList<int> types,
        IReadOnlyList<ManufacturingRequiredSkill> manufacturing
    )
    {
        var required = types
            .SelectMany(type =>
            {
                var attributes = fitting.Attributes(type);
                return InventoryDefinitions.RequiredSkills.Select(skill => new ManufacturingRequiredSkill(
                    (int)attributes.GetValueOrDefault(skill.Skill),
                    (int)attributes.GetValueOrDefault(skill.Level)
                ));
            })
            .Concat(manufacturing)
            .Where(skill => skill.TypeId > 0 && skill.Level > 0)
            .GroupBy(skill => skill.TypeId)
            .Select(skills => skills.MaxBy(skill => skill.Level)!);
        using var world = live.Economy.Begin(new(pilots: [pilot.PilotId]));
        Assert.Equal(AccountCloneGrade.Omega, pilot.Account.CloneGrade);
        foreach (var skill in required)
        {
            var row =
                world.Set<PilotSkill>().Find(pilot.PilotId, skill.TypeId)
                ?? world.Add(new PilotSkill { PilotId = pilot.PilotId, TypeId = skill.TypeId }).Entity;
            var points = TrainedSkill.PointsForLevel(fitting.Attributes(skill.TypeId)[DogmaAttributeId.SkillTimeConstant], skill.Level);
            row.Points = Math.Max(row.Points, points);
        }
        world.Commit();
    }

    private sealed class SuccessfulCapitalInvention : IIndustryRandom
    {
        public IReadOnlyList<double> Rolls(int count) => new double[count];
    }
}
