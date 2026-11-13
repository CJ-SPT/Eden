using System.Globalization;
using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Structures;
using Eden.Protocol.Tests.Structures;
using Eden.World.Catalog;
using Eden.World.Industry;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Industry;

public sealed class ReactionJobTests
{
    private const int Input = 16643;
    private const int Fuel = 4051;
    private const int Athanor = 35835;
    private const int CompositeReactor = 45537;
    private const int HybridReactor = 45538;
    private const int BiochemicalReactor = 45539;

    private sealed class SuccessfulInvention : IIndustryRandom
    {
        public IReadOnlyList<double> Rolls(int count) => Enumerable.Repeat(0d, count).ToArray();
    }

    [Fact]
    public void MoonReactionOutputsAndAnInventedCopyBecomeAMinerTwo()
    {
        using StructureTests.Fixture f = new();
        const int SourceStation = 60015159;
        const int CompoundFormula = 46169;
        const int PolymerFormula = 46167;
        const int CompositeFormula = 46205;
        const int ComponentBlueprint = 17357;
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory VALUES (9,'Blueprint'),(4,'Material');
                INSERT OR IGNORE INTO ItemGroup VALUES (1888,'Composite Formulas',9),(447,'Component Blueprints',9),
                    (427,'Moon Materials',4),(428,'Intermediate Materials',4),(429,'Composites',4);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES
                    (46169,'Crystallite Alloy Formula',1888),(46167,'Carbon Polymers Formula',1888),
                    (46205,'Crystalline Carbonide Formula',1888),(17357,'Photon Microprocessor Blueprint',447),
                    (16640,'Cobalt',427),(16643,'Cadmium',427),(16633,'Hydrocarbons',427),(16636,'Silicates',427),
                    (16655,'Crystallite Alloy',428),(16659,'Carbon Polymers',428),(16670,'Crystalline Carbonide',429),
                    (16680,'Phenolic Composites',429),(16681,'Nanotransistors',429),(33359,'Meta-Materials',429),
                    (11541,'Photon Microprocessor',429),(11399,'Morphite',429),(3689,'Mechanical Parts',429),
                    (11689,'Laser Focusing Crystals',429),(11482,'R.A.M. Energy Tech',429),
                    (483,'Miner I',54),(482,'Miner II',54),(784,'Miner II Blueprint',447),(785,'Miner I Blueprint',447),
                    (11447,'Laser Physics',257),(11452,'Mechanical Engineering',257),(23121,'Gallente Encryption Methods',257),
                    (20424,'Mechanical Datacore',429),(20413,'Laser Physics Datacore',429);
                INSERT OR IGNORE INTO DogmaAttribute VALUES (275,'skillTimeConstant'),(440,'manufacturingTimeBonus');
                INSERT OR IGNORE INTO TypeAttribute VALUES (11447,275,5),(11452,275,5),(23121,275,5),(3380,275,1),(3380,440,-4);
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/systems','30001392','{{"securityStatus":0.4}}'),
                    ('client/stationoperations','14','{{"services":[7,14,15,17]}}'),
                    ('client/typelist','799','{{"includedTypeIDs":[23121]}}'),
                    ('client/blueprints','"785"','{{"blueprintTypeID":785,"maxProductionLimit":200,"activities":{{
                        "copying":{{"time":720}},"invention":{{"time":13800,
                        "skills":[{{"typeID":11447,"level":1}},{{"typeID":11452,"level":1}},{{"typeID":23121,"level":1}}],
                        "materials":[{{"typeID":20424,"quantity":2}},{{"typeID":20413,"quantity":2}}],
                        "products":[{{"typeID":784,"quantity":10,"probability":0.34}}]}}}}}}'),
                    ('client/blueprints','"784"','{{"blueprintTypeID":784,"activities":{{"manufacturing":{{"time":2340,
                        "skills":[{{"typeID":3380,"level":5}},{{"typeID":11447,"level":1}},{{"typeID":11452,"level":1}}],
                        "materials":[{{"typeID":11399,"quantity":1}},{{"typeID":3689,"quantity":8}},{{"typeID":483,"quantity":1}},
                            {{"typeID":11541,"quantity":5}},{{"typeID":11689,"quantity":3}},{{"typeID":11482,"quantity":1}}],
                        "products":[{{"typeID":482,"quantity":1}}]}}}}}}');
                """
            );
            foreach (
                int type in new[]
                {
                    46169,
                    46167,
                    46205,
                    17357,
                    16640,
                    16643,
                    16633,
                    16636,
                    16655,
                    16659,
                    16670,
                    16680,
                    16681,
                    33359,
                    11541,
                    11399,
                    3689,
                    11689,
                    11482,
                    483,
                    482,
                    784,
                    785,
                    20424,
                    20413,
                }
            )
            {
                catalog.Database.ExecuteSqlRaw(
                    "INSERT OR REPLACE INTO SourceRecord VALUES ('client/types',{0},{1})",
                    type.ToString(CultureInfo.InvariantCulture),
                    """{"published":1,"volume":0.01}"""
                );
            }
            foreach (
                var (formula, product, left, right, units) in new[]
                {
                    (CompoundFormula, 16655, 16640, 16643, 200),
                    (PolymerFormula, 16659, 16633, 16636, 200),
                    (CompositeFormula, 16670, 16655, 16659, 10000),
                }
            )
            {
                string data = JsonSerializer.Serialize(
                    new
                    {
                        blueprintTypeID = formula,
                        activities = new
                        {
                            reaction = new
                            {
                                time = 10800,
                                materials = new[]
                                {
                                    new { typeID = 4247, quantity = 5 },
                                    new { typeID = left, quantity = 100 },
                                    new { typeID = right, quantity = 100 },
                                },
                                products = new[] { new { typeID = product, quantity = units } },
                            },
                        },
                    }
                );
                string key = JsonSerializer.Serialize(formula.ToString(CultureInfo.InvariantCulture));
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/blueprints',{key},{data})");
            }
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/blueprints','"17357"',
                    '{{"blueprintTypeID":17357,"activities":{{"manufacturing":{{"time":75,
                        "materials":[{{"typeID":16680,"quantity":6}},{{"typeID":16681,"quantity":2}},
                            {{"typeID":16670,"quantity":17}},{{"typeID":33359,"quantity":2}}],
                        "products":[{{"typeID":11541,"quantity":1}}]}}}}}}');
                """
            );
        }

        using (WorldDbContext world = new(f.Db.Path))
        {
            foreach (var (id, rank, level) in new[] { (3380, 1, 5), (11447, 5, 1), (11452, 5, 1), (23121, 5, 1) })
            {
                var skill =
                    world.Set<PilotSkill>().Find(f.PilotId, id) ?? world.Add(new PilotSkill { PilotId = f.PilotId, TypeId = id }).Entity;
                skill.Points = TrainedSkill.PointsForLevel(rank, level);
            }
            world.SaveChanges();
        }

        var refinery = f.Online(Athanor);
        long reactor = f.Item(CompositeReactor);
        f.Structures.FitService(f.PilotId, refinery.Id, reactor, StructureRules.FirstServiceSlot);
        f.Structures.DepositFuel(f.PilotId, refinery.Id, f.Item(Fuel, 2_000), 2_000);
        f.Structures.SetServiceOnline(f.PilotId, refinery.Id, reactor, true);
        var docking = f.Db.Services.GetRequiredService<IStationDocking>();
        docking.Dock(f.PilotId, f.ShipId, refinery.Id);
        var industry = new PilotIndustryStore(new(f.Db.Path), f.Clock, new SuccessfulInvention());
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        foreach (int material in new[] { 16640, 16643, 16633, 16636 })
        {
            Grant(material, 100, refinery.Id);
        }
        Grant(4247, 15, refinery.Id);
        RunReaction(CompoundFormula, 16655, 16640, 16643);
        RunReaction(PolymerFormula, 16659, 16633, 16636);
        long carbonide = RunReaction(CompositeFormula, 16670, 16655, 16659);
        var loaded = Assert
            .Single(
                inventory.Move(f.PilotId, [carbonide], refinery.Id, f.ShipId, (int)InventoryFlag.Cargo, 85),
                change => change.Item.LocationId == f.ShipId
            )
            .Item;
        var station = f.Db.Services.GetRequiredService<ISystemObjects>().Load(refinery.SystemId).Single(o => o.Id == SourceStation);
        f.Db.Store.SetPosition(f.ShipId, station.Position + new SpacePosition(station.Radius + 500, 0, 0), refinery.SystemId);
        docking.Dock(f.PilotId, f.ShipId, SourceStation);
        inventory.Move(f.PilotId, [loaded.Id], f.ShipId, SourceStation, (int)InventoryFlag.Hangar, null);
        Grant(16680, 30, SourceStation);
        Grant(16681, 10, SourceStation);
        Grant(33359, 10, SourceStation);
        long blueprint = Grant(ComponentBlueprint, 1, SourceStation);
        var manufacture = industry
            .Install(
                f.PilotId,
                new(
                    blueprint,
                    ComponentBlueprint,
                    SourceStation,
                    5,
                    11541,
                    300,
                    new Dictionary<int, int>
                    {
                        [16680] = 30,
                        [16681] = 10,
                        [16670] = 85,
                        [33359] = 10,
                    }
                )
            )
            .Job;
        Assert.Equal(5, Assert.Single(Deliver(manufacture).Changes, i => i.Item.TypeId == 11541).Item.Quantity);

        Grant(3689, 8, SourceStation);
        Grant(483, 1, SourceStation);
        Grant(11689, 3, SourceStation);
        Grant(11482, 1, SourceStation);
        Grant(20424, 2, SourceStation);
        Grant(20413, 2, SourceStation);
        long original = Grant(785, 1, SourceStation);
        var copy = industry
            .Install(
                f.PilotId,
                new(
                    original,
                    785,
                    SourceStation,
                    1,
                    null,
                    720,
                    new Dictionary<int, int>(),
                    Activity: IndustryActivity.Copying,
                    LicensedRuns: 1
                )
            )
            .Job;
        long copyId = Assert.Single(Deliver(copy).Changes).Item.Id;
        var invention = industry
            .Install(
                f.PilotId,
                new(
                    copyId,
                    785,
                    SourceStation,
                    1,
                    784,
                    13800,
                    new Dictionary<int, int> { [20424] = 2, [20413] = 2 },
                    Activity: IndustryActivity.Invention
                )
            )
            .Job;
        long invented = Assert.Single(Deliver(invention).Changes, i => i.Item.TypeId == 784).Item.Id;
        var finalRequest = new IndustryRequest(
            invented,
            784,
            SourceStation,
            1,
            482,
            1797,
            new Dictionary<int, int>
            {
                [11399] = 1,
                [3689] = 8,
                [483] = 1,
                [11541] = 5,
                [11689] = 3,
                [11482] = 1,
            }
        );
        var missing = Assert.Throws<InvalidDataException>(() => industry.Install(f.PilotId, finalRequest));
        Assert.Contains("material", missing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, industry.Blueprint(f.PilotId, invented).RunsRemaining);
        Grant(11399, 1, SourceStation);
        var finished = Deliver(industry.Install(f.PilotId, finalRequest).Job);
        Assert.Single(finished.Changes, i => i.Item.TypeId == 482 && i.Item.Quantity == 1);
        Assert.Equal(9, industry.Blueprint(f.PilotId, invented).RunsRemaining);
        Assert.Empty(industry.Deliver(f.PilotId, finished.Job.Id, refinery.SystemId).Changes);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<SavedMarketUnlock>().Where(row => row.TypeId == 16655 || row.TypeId == 16659 || row.TypeId == 16670));

        IndustryResult Deliver(IndustryJobDetails job)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(job.DurationSeconds));
            return industry.Deliver(f.PilotId, job.Id, refinery.SystemId);
        }

        long RunReaction(int formula, int output, int left, int right)
        {
            var reaction = industry
                .Install(
                    f.PilotId,
                    new(
                        Grant(formula, 1, refinery.Id),
                        formula,
                        refinery.Id,
                        1,
                        output,
                        10800,
                        new Dictionary<int, int>
                        {
                            [4247] = 5,
                            [left] = 100,
                            [right] = 100,
                        },
                        Activity: IndustryActivity.Reaction
                    )
                )
                .Job;
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(reaction.DurationSeconds));
            return Assert.Single(industry.Deliver(f.PilotId, reaction.Id, refinery.SystemId).Changes).Item.Id;
        }

        long Grant(int type, int quantity, long location)
        {
            long id = f.Item(type, quantity);
            using WorldDbContext world = new(f.Db.Path);
            var item = world.Set<OwnedItem>().Find(id)!;
            item.LocationId = location;
            item.FlagId = (int)InventoryFlag.Hangar;
            world.SaveChanges();
            return id;
        }
    }

    [Theory]
    [InlineData(46166, 16650, 1888, CompositeReactor)]
    [InlineData(46157, 30309, 1889, HybridReactor)]
    [InlineData(57496, 57459, 4097, BiochemicalReactor)]
    public void ReactorFamilyReservesFormulaConsumesMaterialsAndDeliversOnceAfterPause(int formulaType, int product, int group, int reactor)
    {
        using StructureTests.Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                INSERT OR IGNORE INTO ItemCategory VALUES(9,'Blueprint'),(4,'Material');
                INSERT OR IGNORE INTO ItemGroup VALUES({group},'Reaction Formulas',9),(428,'Intermediate Materials',4);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES
                    ({formulaType},'Formula',{group}),({product},'Output',428),(16643,'Input',428);
                """
            );
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30001392',{0})",
                """{"securityStatus":0.4}"""
            );
            foreach (int type in new[] { formulaType, product, Input })
            {
                catalog.Database.ExecuteSqlRaw(
                    "INSERT OR REPLACE INTO SourceRecord VALUES ('client/types',{0},{1})",
                    type.ToString(CultureInfo.InvariantCulture),
                    """{"published":1,"marketGroupID":1,"volume":1}"""
                );
            }
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/blueprints',{0},{1})",
                JsonSerializer.Serialize(formulaType.ToString(CultureInfo.InvariantCulture)),
                $$"""
                {"blueprintTypeID":{{formulaType}},"activities":{"reaction":{"time":3600,
                "materials":[{"typeID":16643,"quantity":10},{"typeID":4051,"quantity":5}],
                "products":[{"typeID":{{product}},"quantity":20}]
                } } }
                """
            );
        }

        var structure = f.Online(Athanor);
        long module = f.Item(reactor);
        int otherReactor = reactor == CompositeReactor ? BiochemicalReactor : CompositeReactor;
        long otherModule = f.Item(otherReactor);
        f.Structures.FitService(f.PilotId, structure.Id, module, StructureRules.FirstServiceSlot);
        f.Structures.FitService(f.PilotId, structure.Id, otherModule, StructureRules.FirstServiceSlot + 1);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(Fuel, 5_000), 5_000);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, otherModule, true);
        long formula = MoveToHangar(formulaType, 1);
        long material = MoveToHangar(Input, 10);
        long fuel = MoveToHangar(Fuel, 5);
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);

        var industry = f.Db.Services.GetRequiredService<IPilotIndustry>();
        Assert.Empty(industry.Facility(f.PilotId, structure.Id).ReactionProducts!);
        var request = new IndustryRequest(
            formula,
            formulaType,
            structure.Id,
            1,
            product,
            3600,
            new Dictionary<int, int> { [Input] = 10, [Fuel] = 5 },
            Activity: IndustryActivity.Reaction
        );
        Assert.Throws<InvalidDataException>(() => industry.Install(f.PilotId, request));
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true);
        Assert.Equal(product, Assert.Single(industry.Facility(f.PilotId, structure.Id).ReactionProducts!));
        Assert.Throws<InvalidDataException>(() => industry.Install(f.PilotId, request with { QuotedSeconds = 3599 }));
        var job = industry.Install(f.PilotId, request).Job;
        Assert.Equal(job.Id, industry.Blueprint(f.PilotId, formula).JobId);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, false);
        f.Clock.AdvanceWallTime(TimeSpan.FromHours(2));
        Assert.Equal(IndustryJobStatus.Paused, industry.Job(f.PilotId, job.Id).Status);
        Assert.Throws<InvalidDataException>(() => industry.Deliver(f.PilotId, job.Id, structure.SystemId));
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true);
        f.Clock.AdvanceWallTime(TimeSpan.FromHours(1));
        Assert.Equal(20, Assert.Single(industry.Deliver(f.PilotId, job.Id, structure.SystemId).Changes).Item.Quantity);
        Assert.Empty(industry.Deliver(f.PilotId, job.Id, structure.SystemId).Changes);
        Assert.Null(industry.Blueprint(f.PilotId, formula).JobId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Set<OwnedItem>().Find(material));
        Assert.Null(saved.Set<OwnedItem>().Find(fuel));
        Assert.Empty(saved.Set<SavedMarketUnlock>().Where(row => row.TypeId == product));

        long MoveToHangar(int type, int quantity)
        {
            long id = f.Item(type, quantity);
            using WorldDbContext world = new(f.Db.Path);
            var item = world.Set<OwnedItem>().Find(id)!;
            item.LocationId = structure.Id;
            item.FlagId = (int)InventoryFlag.Hangar;
            world.SaveChanges();
            return id;
        }
    }
}
