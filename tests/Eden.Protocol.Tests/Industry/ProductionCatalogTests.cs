using System.Reflection;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Industry;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Industry;

public sealed class ProductionCatalogTests
{
    private sealed record CatalogType(int Id, string Name, int GroupId, int CategoryId, bool Published);

    private sealed record Attribute(int Id, double Value);

    private sealed record FixtureType(int Id, JsonElement Definition, Attribute[] Attributes);

    private sealed record Ingredient(int TypeID, int Quantity);

    private sealed record Skill(int TypeID, int Level);

    private sealed record Activity(int Time, Ingredient[] Materials, Ingredient[]? Products, Skill[]? Skills);

    private sealed record Recipe(int BlueprintId, int ProductId, bool Admitted, Activity Activity);

    private sealed record InventionProduct(int TypeID, int Quantity, double Probability);

    private sealed record RelicActivity(int Time, Ingredient[] Materials, Skill[] Skills, InventionProduct[] Products);

    private sealed record RelicActivities(RelicActivity Invention);

    private sealed record Relic(int BlueprintTypeID, RelicActivities Activities);

    private sealed record Audit(
        int Build,
        CatalogType[] CatalogTypes,
        FixtureType[] FixtureTypes,
        int[] Samples,
        Recipe[] Recipes,
        Relic Relic,
        JsonElement LowerInventionSkills
    );

    private sealed class FixedRandom(double value) : IIndustryRandom
    {
        public IReadOnlyList<double> Rolls(int count) => Enumerable.Repeat(value, count).ToArray();
    }

    private static readonly Audit Data = Read();

    private static Audit Read()
    {
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream("Eden.Protocol.Tests.Fixtures.ProductionCatalog.json")!;
        return JsonSerializer.Deserialize<Audit>(stream, JsonSerializerOptions.Web)!;
    }

    public static IEnumerable<object[]> Products => Data.Samples.Select(id => new object[] { id });

    private sealed class Fixture : IDisposable
    {
        internal Clock Clock { get; } = new();
        internal TestWorld Db { get; }
        internal IPilotIndustry Industry => Db.Services.GetRequiredService<IPilotIndustry>();
        internal string CatalogPath => new WorldDatabase(Db.Path).CatalogPath;
        internal long PilotId => Db.Store.FindPilot(1000001)!.PilotId;
        internal int StationId => Db.Store.FindPilot(1000001)!.StationId;
        internal int SystemId => Db.Store.FindPilot(1000001)!.SystemId;

        internal Fixture(bool all = false)
        {
            Db = new(clock: Clock);
            var recipes = all ? Data.Recipes : Data.Recipes.Where(r => Data.Samples.Contains(r.BlueprintId)).ToArray();
            var ids = recipes
                .SelectMany(r =>
                    new[] { r.BlueprintId, r.ProductId }
                        .Concat((r.Activity.Materials ?? []).Select(m => m.TypeID))
                        .Concat((r.Activity.Skills ?? []).Select(s => s.TypeID))
                )
                .Append((int)IndustrySkill.Industry)
                .Append((int)IndustrySkill.AdvancedIndustry)
                .ToHashSet();
            ids.Add(Data.Relic.BlueprintTypeID);
            ids.UnionWith(Data.Relic.Activities.Invention.Materials.Select(m => m.TypeID));
            ids.UnionWith(Data.Relic.Activities.Invention.Skills.Select(m => m.TypeID));
            using CatalogDbContext catalog = new(CatalogPath, writable: true);
            using var transaction = catalog.Database.BeginTransaction();
            foreach (var type in Data.CatalogTypes.Where(t => all || ids.Contains(t.Id)))
            {
                catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO ItemCategory VALUES ({type.CategoryId},{type.CategoryId.ToString()})");
                catalog.Database.ExecuteSql(
                    $"INSERT OR IGNORE INTO ItemGroup VALUES ({type.GroupId},{type.GroupId.ToString()},{type.CategoryId})"
                );
                catalog.Database.ExecuteSql(
                    $"INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES ({type.Id},{type.Name},{type.GroupId})"
                );
                string definition = JsonSerializer.Serialize(
                    new
                    {
                        published = type.Published ? 1 : 0,
                        volume = 1,
                        marketGroupID = 1,
                    }
                );
                catalog.Database.ExecuteSql(
                    $"INSERT OR REPLACE INTO SourceRecord VALUES ({"client/types"},{type.Id.ToString()},{definition})"
                );
            }
            foreach (var type in Data.FixtureTypes.Where(t => ids.Contains(t.Id)))
            {
                string definition = type.Definition.GetRawText();
                catalog.Database.ExecuteSql(
                    $"INSERT OR REPLACE INTO SourceRecord VALUES ({"client/types"},{type.Id.ToString()},{definition})"
                );
                foreach (var attribute in type.Attributes)
                {
                    catalog.Database.ExecuteSql(
                        $"INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES ({attribute.Id},{attribute.Id.ToString()})"
                    );
                    catalog.Database.ExecuteSql(
                        $"INSERT OR REPLACE INTO TypeAttribute VALUES ({type.Id},{attribute.Id},{attribute.Value})"
                    );
                }
            }
            foreach (var recipe in recipes)
            {
                string key = JsonSerializer.Serialize(recipe.BlueprintId.ToString());
                string definition = JsonSerializer.Serialize(
                    new { blueprintTypeID = recipe.BlueprintId, activities = new { manufacturing = recipe.Activity } },
                    JsonSerializerOptions.Web
                );
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ({"client/blueprints"},{key},{definition})");
            }
            string lower = Data.LowerInventionSkills.GetRawText();
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ({"client/typelist"},{"799"},{lower})");
            string relicKey = JsonSerializer.Serialize(Data.Relic.BlueprintTypeID.ToString());
            string relicDefinition = JsonSerializer.Serialize(Data.Relic, JsonSerializerOptions.Web);
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ({"client/blueprints"},{relicKey},{relicDefinition})");
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{\"services\":[7,14,15,17]}}')"
            );
            transaction.Commit();
        }

        internal long Grant(int type, int quantity, bool copy = false)
        {
            using WorldDbContext world = new(Db.Path);
            long id = InventoryPersistence.NextId(world);
            world.Add(
                new OwnedItem
                {
                    Id = id,
                    OwnerId = PilotId,
                    TypeId = type,
                    LocationId = StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = quantity,
                    Singleton = copy,
                    BlueprintRuns = copy ? 1 : -1,
                }
            );
            world.SaveChanges();
            return id;
        }

        internal IndustryRequest Prepare(Recipe recipe, bool copy = false)
        {
            long blueprint = Grant(recipe.BlueprintId, 1, copy);
            foreach (var material in recipe.Activity.Materials)
            {
                Grant(material.TypeID, material.Quantity);
            }
            Train((recipe.Activity.Skills ?? []).Select(s => s.TypeID).Append((int)IndustrySkill.Industry));
            double seconds = recipe.Activity.Time * 0.8;
            foreach (var skill in recipe.Activity.Skills ?? [])
            {
                double bonus =
                    Data.FixtureTypes.Single(t => t.Id == skill.TypeID)
                        .Attributes.SingleOrDefault(a => a.Id == (int)DogmaAttributeId.ManufactureTimePerLevel)
                        ?.Value
                    ?? 0;
                seconds *= 1 + bonus * 5 / 100;
            }
            return new(
                blueprint,
                recipe.BlueprintId,
                StationId,
                1,
                recipe.ProductId,
                EconomicRules.ClientRound(seconds, 0),
                recipe.Activity.Materials.ToDictionary(m => m.TypeID, m => m.Quantity)
            );
        }

        internal void Train(IEnumerable<int> skills)
        {
            using WorldDbContext world = new(Db.Path);
            foreach (int skill in skills.Distinct())
            {
                double rank = Data
                    .FixtureTypes.Single(t => t.Id == skill)
                    .Attributes.Single(a => a.Id == (int)DogmaAttributeId.SkillTimeConstant)
                    .Value;
                var saved =
                    world.Set<PilotSkill>().Find(PilotId, skill) ?? world.Add(new PilotSkill { PilotId = PilotId, TypeId = skill }).Entity;
                saved.Points = TrainedSkill.PointsForLevel(rank, 5);
            }
            world.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void EveryPinnedRecipeHasTheRecordedAdmissionWithoutFittingOrTechTierWhitelists()
    {
        using Fixture f = new(all: true);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var actual = ManufacturingCatalog.Recipes(catalog);
        Assert.Equal(3662168, Data.Build);
        Assert.Equal(Data.Recipes.Where(r => r.Admitted).Select(r => r.BlueprintId).Order(), actual.Keys.Order());
        Assert.Equal(Data.Recipes.Where(r => r.Admitted).Select(r => r.ProductId).Distinct().Order(), f.Industry.ProductTypes().Order());
    }

    [Theory]
    [MemberData(nameof(Products))]
    public void CatalogProductsConsumeExactInputsAndDeliverExactlyOnceAcrossRestart(int blueprintId)
    {
        using Fixture f = new();
        var recipe = Data.Recipes.Single(r => r.BlueprintId == blueprintId);
        var request = f.Prepare(recipe, copy: true);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            // Exercise capital recipes at an eligible station; no client travel is performed.
            string security = "{\"securityStatus\":0.2}";
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord VALUES ({"client/systems"},{f.SystemId.ToString()},{security})"
            );
        }
        Assert.Contains(recipe.ProductId, f.Industry.ProductTypes(f.StationId));
        var job = f.Industry.Install(f.PilotId, request).Job;
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.All(
                recipe.Activity.Materials,
                m => Assert.False(world.Set<OwnedItem>().Any(i => i.OwnerId == f.PilotId && i.TypeId == m.TypeID))
            );
        }
        Assert.Throws<InvalidDataException>(() => f.Industry.Deliver(f.PilotId, job.Id, f.SystemId));
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(job.DurationSeconds));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        var delivered = f.Industry.Deliver(f.PilotId, job.Id, f.SystemId);
        var output = Assert.Single(delivered.Changes, c => c.Item.TypeId == recipe.ProductId);
        Assert.Equal(Assert.Single(recipe.Activity.Products!).Quantity, output.Item.Quantity);
        Assert.False(output.Item.Singleton);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Empty(f.Industry.Deliver(f.PilotId, job.Id, f.SystemId).Changes);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Null(reopened.Set<OwnedItem>().Find(request.BlueprintId));
        Assert.Equal(
            output.Item.Quantity,
            reopened.Set<OwnedItem>().Where(i => i.TypeId == recipe.ProductId && i.OwnerId == f.PilotId).Sum(i => i.Quantity)
        );
    }

    [Fact]
    public void UnavailableInputsRemainUnavailableAndRejectedInstallsDoNotConsumeAnything()
    {
        using Fixture f = new();
        var recipe = Data.Recipes.Single(r => r.ProductId == 482);
        var request = f.Prepare(recipe);
        int missing = recipe.Activity.Materials.Last().TypeID;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Where(i => i.OwnerId == f.PilotId && i.TypeId == missing).ExecuteDelete();
            world.Set<SavedWorldEconomy>().Single().TestingMarket = false;
            world.SaveChanges();
        }
        string before;
        using (WorldDbContext world = new(f.Db.Path))
        {
            before = JsonSerializer.Serialize(world.Set<OwnedItem>().OrderBy(i => i.Id).ToArray());
        }
        Assert.Throws<InvalidDataException>(() => f.Industry.Install(f.PilotId, request));
        Assert.Empty(f.Industry.Jobs(f.PilotId, true));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(before, JsonSerializer.Serialize(saved.Set<OwnedItem>().OrderBy(i => i.Id).ToArray()));
        Assert.Empty(saved.Set<NpcMarketOrder>().Where(o => o.TypeId == missing && o.Remaining > 0));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public void AncientRelicStacksReserveAttemptsAndConsumeUnitsWithoutRerolling(bool cancel, bool success, bool exhaust)
    {
        using Fixture f = new();
        var recipe = Data.Relic.Activities.Invention;
        var product = recipe.Products[0];
        int sourceUnits = exhaust ? 2 : 3;
        long relic = f.Grant(Data.Relic.BlueprintTypeID, sourceUnits);
        foreach (var material in recipe.Materials)
        {
            f.Grant(material.TypeID, material.Quantity * 2);
        }
        f.Train(recipe.Skills.Select(s => s.TypeID));
        PilotIndustryStore industry = new(new(f.Db.Path), f.Clock, new FixedRandom(success ? 0 : 0.99));
        Assert.Equal(sourceUnits, industry.Blueprint(f.PilotId, relic).RunsRemaining);
        Assert.Contains(industry.Blueprints(f.PilotId), b => b.Item.Id == relic);
        var request = new IndustryRequest(
            relic,
            Data.Relic.BlueprintTypeID,
            f.StationId,
            2,
            product.TypeID,
            recipe.Time * 2,
            recipe.Materials.ToDictionary(m => m.TypeID, m => m.Quantity * 2),
            Activity: IndustryActivity.Invention
        );
        var job = industry.Install(f.PilotId, request).Job;
        Assert.True(job.BlueprintCopy);
        Assert.Null(job.SuccessfulRuns);
        long extra = f.Grant(Data.Relic.BlueprintTypeID, 2);
        Assert.Empty(f.Db.Services.GetRequiredService<IPilotInventoryStacks>().StackAll(f.PilotId, f.StationId, (int)InventoryFlag.Hangar));
        var pilot = f.Db.Store.FindPilot(1000001)!;
        Assert.Throws<InvalidDataException>(() =>
            f
                .Db.Services.GetRequiredService<IPilotInventory>()
                .Move(f.PilotId, [relic], f.StationId, pilot.ShipId, (int)InventoryFlag.Cargo, null)
        );
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        industry = new(new(f.Db.Path), f.Clock, new FixedRandom(success ? 0.99 : 0));
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(job.DurationSeconds));
        var completed = cancel ? industry.Cancel(f.PilotId, job.Id, f.SystemId) : industry.Deliver(f.PilotId, job.Id, f.SystemId);
        var copies = completed.Changes.Where(c => c.Item.TypeId == product.TypeID).ToArray();
        Assert.Equal(!cancel && success ? 2 : 0, copies.Length);
        foreach (var copy in copies)
        {
            var blueprint = industry.Blueprint(f.PilotId, copy.Item.Id);
            Assert.Equal(product.Quantity, blueprint.RunsRemaining);
            Assert.Equal(2, blueprint.MaterialEfficiency);
            Assert.Equal(4, blueprint.TimeEfficiency);
        }
        if (exhaust)
        {
            Assert.Throws<InvalidDataException>(() => industry.Blueprint(f.PilotId, relic));
        }
        else
        {
            Assert.Equal(1, industry.Blueprint(f.PilotId, relic).RunsRemaining);
        }
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Equal(exhaust ? null : 1, saved.Set<OwnedItem>().Find(relic)?.Quantity);
            Assert.Equal(2, saved.Set<OwnedItem>().Find(extra)!.Quantity);
            Assert.All(
                recipe.Materials,
                m => Assert.Empty(saved.Set<OwnedItem>().Where(i => i.TypeId == m.TypeID && i.OwnerId == f.PilotId))
            );
        }
        Assert.Empty((cancel ? industry.Cancel(f.PilotId, job.Id, f.SystemId) : industry.Deliver(f.PilotId, job.Id, f.SystemId)).Changes);
    }

    [Fact]
    public void AdvancedInputReferencePricesAreBackfilledWithoutChangingSavedOverridesOrCreatingStock()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            world.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
            world.Set<SavedAdjustedPrice>().ExecuteDelete();
            world.Add(new SavedAdjustedPrice { TypeId = 34, PriceIsk = 123.45m });
            world.SaveChanges();
            world.Database.ExecuteSqlRaw("PRAGMA user_version=129");
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(123.45m, saved.Set<SavedAdjustedPrice>().Find(34)!.PriceIsk);
        Assert.True(saved.Set<SavedAdjustedPrice>().Find(11541)!.PriceIsk > 0);
        Assert.Empty(saved.Set<OwnedItem>().Where(i => i.TypeId == 11541));
        Assert.Empty(saved.Set<NpcMarketOrder>().Where(o => o.TypeId == 11541));
    }

    [Fact]
    public void AdvancedManufacturingChargesComponentsAtomicallyAndUnlocksOnlyOnClaim()
    {
        using Fixture f = new();
        var recipe = Data.Recipes.Single(r => r.ProductId == 482);
        var request = f.Prepare(recipe, copy: true);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            string security = "{\"securityStatus\":1.0}";
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord VALUES ({"client/systems"},{f.SystemId.ToString()},{security})"
            );
        }
        ManufacturingCharge charge;
        const long balance = 100_000_000_000;
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            world.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
            var wallet = world.Set<PilotWallet>().Find(f.PilotId) ?? world.Add(new PilotWallet { PilotId = f.PilotId }).Entity;
            wallet.Cents = balance;
            world.SaveChanges();
            EconomicStore.ReconcileIndustryPrices(world, catalog);
            Assert.All(request.QuotedMaterials.Keys, id => Assert.True(world.Set<SavedAdjustedPrice>().Find(id)!.PriceIsk > 0));
            charge = EconomicStore.Manufacturing(world, f.PilotId, f.SystemId, request.QuotedMaterials, 1);
        }
        Assert.True(charge.TotalCents > 0);
        Assert.Throws<InvalidDataException>(() => f.Industry.Install(f.PilotId, request with { QuotedCharge = new(0, 0, 0) }));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(balance, world.Set<PilotWallet>().Find(f.PilotId)!.Cents);
            Assert.Empty(world.Set<SavedMarketUnlock>());
            Assert.All(
                request.QuotedMaterials,
                material =>
                    Assert.Equal(
                        material.Value,
                        world.Set<OwnedItem>().Where(i => i.TypeId == material.Key && i.OwnerId == f.PilotId).Sum(i => i.Quantity)
                    )
            );
        }
        var job = f.Industry.Install(f.PilotId, request with { QuotedCharge = charge }).Job;
        Assert.Equal(charge.TotalCents, job.CostCents);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(balance - charge.TotalCents, world.Set<PilotWallet>().Find(f.PilotId)!.Cents);
            Assert.Empty(world.Set<SavedMarketUnlock>());
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(job.DurationSeconds));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        f.Industry.Deliver(f.PilotId, job.Id, f.SystemId);
        Assert.Empty(f.Industry.Deliver(f.PilotId, job.Id, f.SystemId).Changes);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(balance - charge.TotalCents, saved.Set<PilotWallet>().Find(f.PilotId)!.Cents);
        Assert.Equal(job.Id, saved.Set<SavedMarketUnlock>().Find(recipe.ProductId)!.JobId);
        Assert.Single(saved.Set<OwnedItem>().Where(i => i.TypeId == recipe.ProductId && i.OwnerId == f.PilotId));
    }

    [Fact]
    public void NativeFacilityListsBroaderProductsAndRelicsExposeTheirActualUnits()
    {
        using Fixture f = new();
        long relic = f.Grant(Data.Relic.BlueprintTypeID, 3);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new(), []),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        IndustryHandler handler = new(f.Industry);
        var facility = Assert.IsType<Dictionary<string, object?>>(
            BlueMarshal.Decode(BlueMarshal.Encode(handler.CallWire(context, "facilityManager", "GetFacility", [(long)f.StationId])))
        );
        var activities = Assert.IsType<Dictionary<long, object?>>(facility["activities"]);
        var manufacturing = Assert.IsType<object?[]>(activities[(long)IndustryActivity.Manufacturing]);
        var products = Assert.IsType<List<object?>>(manufacturing[5]);
        Assert.Contains(482L, products);
        Assert.Contains(29984L, products);
        Assert.DoesNotContain(19722L, products);
        var blueprint = Assert.IsType<BlueInstance>(
            BlueMarshal.Decode(BlueMarshal.Encode(handler.CallWire(context, "blueprintManager", "GetBlueprintData", [relic])))
        );
        var fields = Assert.IsType<Dictionary<string, object?>>(blueprint.State);
        Assert.Equal(3L, fields["quantity"]);
        Assert.Equal(3L, fields["runs"]);
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public void HighSecurityCapitalRejectionPreservesBlueprintAndEveryInput()
    {
        using Fixture f = new();
        var recipe = Data.Recipes.Single(r => r.ProductId == 19722);
        var request = f.Prepare(recipe);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            string security = "{\"securityStatus\":1.0}";
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord VALUES ({"client/systems"},{f.SystemId.ToString()},{security})"
            );
        }
        Assert.DoesNotContain(recipe.ProductId, f.Industry.ProductTypes(f.StationId));
        Assert.Throws<InvalidDataException>(() => f.Industry.Install(f.PilotId, request));
        Assert.Empty(f.Industry.Jobs(f.PilotId, true));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(f.Industry.Blueprint(f.PilotId, request.BlueprintId).JobId);
        Assert.All(
            request.QuotedMaterials,
            material =>
                Assert.Equal(
                    material.Value,
                    saved.Set<OwnedItem>().Where(i => i.TypeId == material.Key && i.OwnerId == f.PilotId).Sum(i => i.Quantity)
                )
        );
    }
}
