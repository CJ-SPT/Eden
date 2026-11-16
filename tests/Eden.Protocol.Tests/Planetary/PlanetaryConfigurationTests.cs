using System.Text.Json;
using System.Text.Json.Nodes;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Planetary;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Planetary;
using Eden.Server.Administration;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Planetary;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Planetary.PlanetaryIndustryTests;

namespace Eden.Protocol.Tests.Planetary;

public sealed class PlanetaryConfigurationTests
{
    [Fact]
    public void CapacityMultipliersIndependentlyAdmitAndRejectColonyLayouts()
    {
        using var f = Prepare();
        var catalog = Catalog(f);
        PlanetaryPlanet planet = new(PlanetId, 1, PlanetaryResources.Barren, 1, 6000000, SpacePosition.Zero, true);
        var empty = PlanetaryRules.Empty(f.PilotId, PlanetId, f.Clock.Now);
        PlanetaryCommand[] layout =
        [
            new CreatePlanetaryPin(new(1), CommandCenter, 1, 1),
            new CreatePlanetaryPin(new(2, 1), Launchpad, 1, 1.001),
            new CreatePlanetaryPin(new(3, 1), Extractor, 1, 1.002),
            new AddPlanetaryHead(new(3, 1), new(0, 1, 1.002)),
            new CreatePlanetaryLink(new(2, 1), new(3, 1), 0),
        ];
        Assert.Throws<InvalidDataException>(() => PlanetaryRules.Edit(empty, planet, catalog, layout, 0, f.Clock.Now));
        var policy = new PlanetaryPolicy(CpuMultiplier: 3, PowergridMultiplier: 2);
        var result = PlanetaryRules.Edit(empty, planet, catalog, layout, 0, f.Clock.Now, policy).Colony;
        Assert.Equal(3, result.Pins.Count);
        Assert.Throws<InvalidDataException>(() => PlanetaryRules.Validate(result, planet, catalog, policy with { CpuMultiplier = 1 }));
        Assert.Throws<InvalidDataException>(() =>
            PlanetaryRules.Validate(result, planet, catalog, policy with { PowergridMultiplier = .1m })
        );
        Assert.Empty(empty.Pins);
    }

    [Theory]
    [InlineData(.1)]
    [InlineData(1)]
    [InlineData(2.5)]
    [InlineData(100)]
    public void EverySchematicScalesOnlyOutputAndKeepsRoutesStorageAndDeadlines(decimal multiplier)
    {
        using var f = Prepare();
        var catalog = Catalog(f);
        var policy = new PlanetaryPolicy(FactoryOutputMultiplier: multiplier);
        foreach (var recipe in catalog.Schematics.Values)
        {
            long start = f.Clock.Now;
            int expected = Math.Max(1, (int)decimal.Floor(recipe.Output.Quantity * multiplier));
            PlanetaryPin store = new(1, Launchpad, 1, 1, start, recipe.Inputs.ToArray());
            PlanetaryPin processor = new(2, recipe.PinTypes.First(), 1, 1.001, start, [], recipe.Id);
            var routes = recipe
                .Inputs.Select((input, index) => new PlanetaryRoute(index + 1, [1, 2], input.TypeId, input.Quantity))
                .Append(new(recipe.Inputs.Count + 1, [2, 1], recipe.Output.TypeId, expected))
                .ToArray();
            PlanetaryColony colony = new(f.PilotId, PlanetId, 5, start, 10, 10, [store, processor], [new(1, 2, 0)], routes);
            var running = PlanetarySimulation.Advance(colony, catalog, start, policy);
            Assert.Empty(running.Pins[0].Contents);
            long completion = start + recipe.CycleSeconds * TimeSpan.TicksPerSecond;
            Assert.Equal(completion, running.Pins[1].NextRunAt);
            var finished = PlanetarySimulation.Advance(running, catalog, completion, policy);
            int fits = (int)Math.Floor(catalog.Pins[Launchpad].Capacity / catalog.Volumes[recipe.Output.TypeId]);
            Assert.Equal(Math.Min(fits, expected), PlanetarySimulation.Quantity(finished.Pins[0], recipe.Output.TypeId));
            Assert.Null(finished.Pins[1].NextRunAt);
            Assert.Equal(
                JsonSerializer.Serialize(finished),
                JsonSerializer.Serialize(PlanetarySimulation.Advance(finished, catalog, completion, policy))
            );
            Assert.Equal(recipe.Inputs, store.Contents);
        }
    }

    [Fact]
    public async Task PendingPolicyActivatesAtRestartAndResidentColonySurvivesCheckpoint()
    {
        using var f = Prepare();
        WorldDatabase database = new(f.Db.Path);
        WorldConfigurationStore configuration = new(database, f.Clock);
        var initial = configuration.Read();
        var legacy = JsonNode.Parse(configuration.Export(initial.Active.Id))!;
        legacy["settings"]!.AsObject().Remove("planetaryIndustry");
        var imported = configuration.Import(initial.Generation, legacy.ToJsonString(), "legacy");
        Assert.Equal(new PlanetaryPolicy(), imported.Pending!.Draft.Settings.PlanetaryIndustry);
        PlanetaryPolicy policy = new(2, 3, 2, 2, 8);
        var pending = configuration.Save(
            imported.Generation,
            initial.Active.Draft with
            {
                Settings = initial.Active.Draft.Settings with { PlanetaryIndustry = policy },
            },
            "test"
        );
        Assert.Equal(new PlanetaryPolicy(), database.Settings.PlanetaryIndustry);
        WorldConfigurationStore.Activate(database);
        Assert.Equal(policy, database.Settings.PlanetaryIndustry);
        Assert.Equal(pending.Pending!.Id, configuration.Read().Active.Id);
        string saved;
        using (LiveWorld live = new(database, f.Clock, combatObjects: false, simulationObjects: false))
        {
            var colony = Deploy(f);
            var extractor = colony.Pins.Single(p => p.TypeId == Extractor);
            var catalog = Catalog(f);
            PlanetaryPlanet planet = new(PlanetId, 1, PlanetaryResources.Barren, 1, 6000000, SpacePosition.Zero, true);
            var normal = PlanetaryRules.Quote(
                planet,
                catalog,
                extractor,
                PlanetaryResources.AqueousLiquids,
                extractor.Heads!,
                PlanetaryRules.MinimumHeadRadius,
                colony.Pins
            );
            Assert.Equal(normal.BaseQuantity * 2, extractor.Program!.BaseQuantity);
            var industry = PlanetaryIndustryTests.Industry(f);
            var quote = industry.Quote(
                f.PilotId,
                PlanetId,
                new(extractor.Id),
                PlanetaryResources.AqueousLiquids,
                extractor.Heads!,
                PlanetaryRules.MinimumHeadRadius
            );
            Assert.Equal(extractor.Program.BaseQuantity, quote.BaseQuantity);
            var processor = colony.Pins.Single(p => p.TypeId == Processor);
            var port = colony.Pins.Single(p => p.TypeId == Launchpad);
            var route = colony.Routes.Single(r => r.Path[0] == processor.Id);
            colony = industry
                .Update(
                    f.PilotId,
                    PlanetId,
                    [
                        new RemovePlanetaryRoute(new(route.Id)),
                        new CreatePlanetaryRoute(new(1, 2), [new(processor.Id), new(port.Id)], Water, 60),
                    ]
                )
                .Colony;
            // Seven launchpads exceed the ordinary level-four CPU budget but fit the tuned one.
            colony = industry
                .Update(
                    f.PilotId,
                    PlanetId,
                    Enumerable
                        .Range(0, 6)
                        .Select(index => (PlanetaryCommand)new CreatePlanetaryPin(new(index + 10, 1), Launchpad, 1, 1.01 + index * .001))
                        .ToArray()
                )
                .Colony;
            string beforeRejectedEdit = JsonSerializer.Serialize(colony);
            Assert.Throws<InvalidDataException>(() =>
                industry.Update(
                    f.PilotId,
                    PlanetId,
                    Enumerable
                        .Range(0, 20)
                        .Select(index => (PlanetaryCommand)new CreatePlanetaryPin(new(index + 20, 1), Launchpad, 1, 1.1 + index * .001))
                        .ToArray()
                )
            );
            Assert.Equal(beforeRejectedEdit, JsonSerializer.Serialize(industry.Read(f.PilotId, PlanetId).Colony));
            var expected = PlanetarySimulation.Advance(colony, catalog, f.Clock.Now + TimeSpan.TicksPerHour, policy);
            f.Clock.Seconds += 3600;
            var actual = industry.Read(f.PilotId, PlanetId).Colony!;
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
            Assert.True(PlanetarySimulation.Quantity(actual.Pins.Single(p => p.Id == port.Id), Water) >= 60);
            saved = JsonSerializer.Serialize(actual);
            await live.FlushAsync();
        }
        WorldDatabase reopenedDatabase = new(f.Db.Path);
        Assert.Equal(policy, reopenedDatabase.Settings.PlanetaryIndustry);
        using LiveWorld reopenedWorld = new(reopenedDatabase, f.Clock, combatObjects: false, simulationObjects: false);
        var reopened = new PilotPlanetaryIndustryStore(reopenedDatabase, f.Clock, f.Db.Services.GetRequiredService<PilotTrainingStore>());
        Assert.Equal(saved, JsonSerializer.Serialize(reopened.Read(f.PilotId, PlanetId).Colony));
        Assert.Equal(saved, JsonSerializer.Serialize(reopened.Read(f.PilotId, PlanetId).Colony));
    }

    [Fact]
    public void PolicyRoundTripsThroughTheFormAndRejectsInvalidImportedValues()
    {
        using var f = Prepare();
        WorldConfigurationStore configuration = new(new(f.Db.Path), f.Clock);
        var draft = configuration.Read().Active.Draft;
        var settings = draft.Settings with { PlanetaryIndustry = new(.1m, 100, 2, 3, 8) };
        Assert.Equal(settings.PlanetaryIndustry, SettingsForm.From(settings).Build().PlanetaryIndustry);
        Assert.Equal(5, ConfigurationDisplay.Compare(draft, draft with { Settings = settings }).Count);
        configuration.Validate(draft with { Settings = settings });
        PlanetaryPolicy[] invalid =
        [
            new(0),
            new(FactoryOutputMultiplier: 101),
            new(CpuMultiplier: -.1m),
            new(PowergridMultiplier: 0),
            new(MaximumPlanets: -1),
            new(MaximumPlanets: PlanetaryRules.MaximumConfiguredPlanets + 1),
        ];
        foreach (var policy in invalid)
        {
            Assert.Throws<InvalidDataException>(() =>
                configuration.Validate(draft with { Settings = draft.Settings with { PlanetaryIndustry = policy } })
            );
        }
        Assert.Throws<InvalidDataException>(() =>
            configuration.Validate(draft with { Settings = draft.Settings with { PlanetaryIndustry = null! } })
        );
        var legacy = JsonNode.Parse(configuration.Export(configuration.Read().Active.Id))!;
        legacy["settings"]!["planetaryIndustry"]!.AsObject().Remove("maximumPlanets");
        var imported = configuration.Import(configuration.Read().Generation, legacy.ToJsonString(), "legacy");
        Assert.Equal(0, imported.Pending!.Draft.Settings.PlanetaryIndustry.MaximumPlanets);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(0, 5, 6)]
    [InlineData(8, 0, 8)]
    [InlineData(2, 5, 2)]
    public async Task ResidentPlanetLimitEnforcesAdmissionWithoutChangingRejectedState(int maximum, int skill, int expected)
    {
        using var f = Prepare();
        f.Train(PlanetaryRules.InterplanetaryConsolidationSkill, skill);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            for (int index = 1; index <= expected; index++)
            {
                int planet = PlanetId + index;
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z)
                    SELECT {planet},SolarSystemId,TypeId,GroupId,Radius,X,Y,Z FROM Celestial WHERE Id={PlanetId}
                    """
                );
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT INTO SourceRecord(Dataset,Key,Data)
                    SELECT Dataset,{planet.ToString()},Data FROM SourceRecord WHERE Dataset='sde/mapPlanets' AND Key={PlanetId.ToString()}
                    """
                );
            }
        }
        long[] centers = Enumerable.Range(0, expected + 1).Select(_ => f.Grant(CommandCenter, cargo: true)).ToArray();
        long replacement = f.Grant(CommandCenter, cargo: true);
        WorldDatabase database = new(f.Db.Path);
        WorldConfigurationStore configuration = new(database, f.Clock);
        var active = configuration.Read();
        configuration.Save(
            active.Generation,
            active.Active.Draft with
            {
                Settings = active.Active.Draft.Settings with { PlanetaryIndustry = new(MaximumPlanets: maximum) },
            },
            "test"
        );
        WorldConfigurationStore.Activate(database);
        using (LiveWorld live = new(database, f.Clock, combatObjects: false, simulationObjects: false))
        {
            PilotPlanetaryIndustryStore industry = new(database, f.Clock, f.Db.Services.GetRequiredService<PilotTrainingStore>());
            for (int index = 0; index < expected; index++)
            {
                Assert.Single(
                    industry
                        .Update(f.PilotId, PlanetId + index, [new CreatePlanetaryPin(new(centers[index]), CommandCenter, 1, 1)])
                        .Colony.Pins
                );
            }
            var before = WorldActionScope.Inspect(
                database,
                world => new
                {
                    Balance = world.Set<PilotWallet>().Find(f.PilotId)!.Cents,
                    Identity = world.Set<SavedPlanetaryIdentity>().Find(1)!.NextPinId,
                    Cargo = world.Set<OwnedItem>().Find(centers[^1])!.Quantity,
                }
            );
            var error = Assert.Throws<InvalidDataException>(() =>
                industry.Update(f.PilotId, PlanetId + expected, [new CreatePlanetaryPin(new(centers[^1]), CommandCenter, 1, 1)])
            );
            Assert.Contains($"limit of {expected} planets", error.Message);
            Assert.Equal(expected, industry.List(f.PilotId).Count);
            Assert.Null(industry.Read(f.PilotId, PlanetId + expected).Colony);
            WorldActionScope.Inspect(
                database,
                world =>
                {
                    Assert.Equal(before.Balance, world.Set<PilotWallet>().Find(f.PilotId)!.Cents);
                    Assert.Equal(before.Identity, world.Set<SavedPlanetaryIdentity>().Find(1)!.NextPinId);
                    Assert.Equal(before.Cargo, world.Set<OwnedItem>().Find(centers[^1])!.Quantity);
                    return true;
                }
            );
            industry.Abandon(f.PilotId, PlanetId + expected - 1);
            Assert.Single(
                industry.Update(f.PilotId, PlanetId + expected, [new CreatePlanetaryPin(new(centers[^1]), CommandCenter, 1, 1)]).Colony.Pins
            );
            // Put the replacement back on the original planet for checkpoint assertions.
            industry.Abandon(f.PilotId, PlanetId + expected);
            industry.Update(f.PilotId, PlanetId + expected - 1, [new CreatePlanetaryPin(new(replacement), CommandCenter, 1, 1)]);
            await live.FlushAsync();
        }
        // A lower limit and later restoration of skill-based limits grandfather saved colonies.
        foreach (int lowered in new[] { 1, 0 })
        {
            WorldDatabase reopened = new(f.Db.Path);
            var current = configuration.Read();
            configuration.Save(
                current.Generation,
                current.Active.Draft with
                {
                    Settings = current.Active.Draft.Settings with { PlanetaryIndustry = new(MaximumPlanets: lowered) },
                },
                "test"
            );
            WorldConfigurationStore.Activate(reopened);
            using LiveWorld live = new(reopened, f.Clock, combatObjects: false, simulationObjects: false);
            PilotPlanetaryIndustryStore industry = new(reopened, f.Clock, f.Db.Services.GetRequiredService<PilotTrainingStore>());
            Assert.Equal(expected, industry.List(f.PilotId).Count);
            var colony = industry.Read(f.PilotId, PlanetId).Colony!;
            Assert.Equal(1, industry.Update(f.PilotId, PlanetId, [new UpgradePlanetaryCommand(new(colony.Pins[0].Id), 1)]).Colony.Level);
            if (expected >= PlanetaryRules.PlanetLimit(reopened.Settings.PlanetaryIndustry, skill))
            {
                Assert.Throws<InvalidDataException>(() =>
                    industry.Update(f.PilotId, PlanetId + expected, [new CreatePlanetaryPin(new(centers[^1]), CommandCenter, 1, 1)])
                );
            }
            await live.FlushAsync();
        }
    }
}
