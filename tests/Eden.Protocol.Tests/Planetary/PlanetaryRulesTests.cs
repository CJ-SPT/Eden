using System.Text.Json;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Planetary;

namespace Eden.Protocol.Tests.Planetary;

public sealed class PlanetaryRulesTests
{
    [Fact]
    public void SurveyResolutionAndRemoteRangeComeFromSavedSkills()
    {
        PlanetaryPlanet planet = new(PlanetaryIndustryTests.PlanetId, 1, PlanetaryResources.Barren, 1, 6000000, SpacePosition.Zero, true);
        var local = new PlanetarySurveyAccess(true, true, null, null);
        var basic = PlanetaryResources.Survey(planet, PlanetaryResources.AqueousLiquids, 30, new(1, 0, 0), local);
        Assert.Equal(15, basic.Bands);
        Assert.Equal(225, basic.Coefficients.Count);
        var full = PlanetaryResources.Survey(planet, PlanetaryResources.AqueousLiquids, 30, new(5, 5, 5), local);
        Assert.Equal(30, full.Bands);
        const double LightYear = 9_460_730_472_580_800;
        var distant = new PlanetarySurveyAccess(false, true, SpacePosition.Zero, new(3 * LightYear, 0, 0));
        Assert.Throws<InvalidDataException>(() =>
            PlanetaryResources.Survey(planet, PlanetaryResources.AqueousLiquids, 30, new(1, 5, 5), distant)
        );
        Assert.Equal(20, PlanetaryResources.Survey(planet, PlanetaryResources.AqueousLiquids, 30, new(2, 5, 5), distant).Bands);
        Assert.Throws<InvalidDataException>(() => PlanetaryResources.Proximity(5, distant with { RemoteAllowed = false }));
        Assert.Equal(4, PlanetaryResources.Proximity(1, distant with { LocalOrOwned = true }));
    }

    [Fact]
    public void LinkUpgradesUseNativeRoundedDistanceCostsAndDoubledBandwidth()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        var profile = PlanetaryIndustryTests.Catalog(f).Link;
        var usage = PlanetaryRules.LinkUsage(profile, 1, 3);
        Assert.Equal(17, usage.Cpu);
        Assert.Equal(11, usage.Power);
        Assert.Equal(10000, PlanetaryRules.Bandwidth(profile, 3));
    }

    [Theory]
    [InlineData(PlanetaryResources.Temperate)]
    [InlineData(PlanetaryResources.Ice)]
    [InlineData(PlanetaryResources.Gas)]
    [InlineData(PlanetaryResources.Oceanic)]
    [InlineData(PlanetaryResources.Lava)]
    [InlineData(PlanetaryResources.Barren)]
    [InlineData(PlanetaryResources.Storm)]
    [InlineData(PlanetaryResources.Plasma)]
    public void EachPlanetHasFiveResourcesAndMatchingBuildings(int planetType)
    {
        using var f = PlanetaryIndustryTests.Prepare();
        var catalog = PlanetaryIndustryTests.Catalog(f);
        var resources = PlanetaryResources.Types(planetType);
        Assert.Equal(5, resources.Distinct().Count());
        Assert.All(resources, type => Assert.Equal(0, catalog.CommodityTiers[type]));
        Assert.Single(catalog.Pins.Values, p => p.Kind == PlanetaryPinKind.Command && p.PlanetTypeId == planetType);
        Assert.Single(catalog.Pins.Values, p => p.Kind == PlanetaryPinKind.Extractor && p.PlanetTypeId == planetType);
        var center = catalog.Pins.Values.Single(p => p.Kind == PlanetaryPinKind.Command && p.PlanetTypeId == planetType);
        var planet = new PlanetaryPlanet(PlanetaryIndustryTests.PlanetId, 1, planetType, 1, 6000000, SpacePosition.Zero, true);
        var colony = PlanetaryRules
            .Edit(
                PlanetaryRules.Empty(1, planet.Id, f.Clock.Now),
                planet,
                catalog,
                [new CreatePlanetaryPin(new(1), center.TypeId, 1, 1)],
                0,
                f.Clock.Now
            )
            .Colony;
        Assert.Equal(center.TypeId, Assert.Single(colony.Pins).TypeId);
    }

    [Theory]
    [InlineData(0.01, 3600)]
    [InlineData(0.05, 1209600)]
    public void ProgramDurationBoundariesUseNativeCycleIntervals(double radius, long seconds)
    {
        using var f = PlanetaryIndustryTests.Prepare();
        var catalog = PlanetaryIndustryTests.Catalog(f);
        var type = catalog.Pins[PlanetaryIndustryTests.Extractor];
        PlanetaryPin pin = new(1, type.TypeId, 1, 1, f.Clock.Now, []);
        PlanetaryPlanet planet = new(PlanetaryIndustryTests.PlanetId, 1, PlanetaryResources.Barren, 1, 6000000, SpacePosition.Zero, true);
        var quote = PlanetaryRules.Quote(planet, catalog, pin, PlanetaryResources.AqueousLiquids, [new(0, 1, 1)], radius, []);
        Assert.Equal(seconds * TimeSpan.TicksPerSecond, quote.CycleTicks * quote.Cycles);
        Assert.True(quote.BaseQuantity > 0);
        var program = new PlanetaryProgram(
            PlanetaryResources.AqueousLiquids,
            f.Clock.Now,
            f.Clock.Now + quote.CycleTicks * quote.Cycles,
            quote.CycleTicks,
            quote.BaseQuantity,
            radius
        );
        Assert.True(PlanetaryRules.ExtractionOutput(program, type, f.Clock.Now + quote.CycleTicks) > 0);
    }

    [Fact]
    public void OfflineCatchupMatchesRepeatedDeadlineAdvancementAndStopsAtProgramExpiry()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        var initial = PlanetaryIndustryTests.Deploy(f);
        var catalog = PlanetaryIndustryTests.Catalog(f);
        long end = f.Clock.Now + 12 * TimeSpan.TicksPerHour;
        var once = PlanetarySimulation.Advance(initial, catalog, end);
        var stepped = initial;
        for (long now = f.Clock.Now + TimeSpan.TicksPerMinute; now <= end; now += TimeSpan.TicksPerMinute)
        {
            stepped = PlanetarySimulation.Advance(stepped, catalog, now);
            Assert.Equal(JsonSerializer.Serialize(stepped), JsonSerializer.Serialize(PlanetarySimulation.Advance(stepped, catalog, now)));
        }
        Assert.Equal(JsonSerializer.Serialize(once), JsonSerializer.Serialize(stepped));
        Assert.All(once.Pins, p => Assert.Null(p.NextRunAt));
        Assert.True(
            PlanetarySimulation.Quantity(once.Pins.Single(p => p.TypeId == PlanetaryIndustryTests.Launchpad), PlanetaryIndustryTests.Water)
                > 0
        );
    }

    [Fact]
    public void ExpeditedTransferIsAtomicAndHasAVolumeBasedSourceCooldown()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        var catalog = PlanetaryIndustryTests.Catalog(f);
        long now = f.Clock.Now;
        PlanetaryPin source = new(1, PlanetaryIndustryTests.Storage, 1, 1, now, [new(PlanetaryIndustryTests.Water, 100)]);
        PlanetaryPin destination = new(2, PlanetaryIndustryTests.Storage, 1, 1.001, now, []);
        PlanetaryColony colony = new(f.PilotId, PlanetaryIndustryTests.PlanetId, 5, now, 10, 10, [source, destination], [new(1, 2, 0)], []);
        var transfer = PlanetarySimulation.Transfer(colony, catalog, [1, 2], [new(PlanetaryIndustryTests.Water, 100)], now);
        Assert.Empty(transfer.Colony.Pins[0].Contents);
        Assert.Equal(100, Assert.Single(transfer.Colony.Pins[1].Contents).Quantity);
        Assert.Equal(now + 5 * TimeSpan.TicksPerMinute, transfer.SourceReadyAt);
        Assert.Throws<InvalidDataException>(() =>
            PlanetarySimulation.Transfer(transfer.Colony, catalog, [1, 2], [new(PlanetaryIndustryTests.Water, 1)], now)
        );
        Assert.Throws<InvalidDataException>(() =>
            PlanetarySimulation.Transfer(colony, catalog, [1, 2], [new(PlanetaryIndustryTests.Water, 101)], now)
        );
        Assert.Equal(100, Assert.Single(source.Contents).Quantity);
    }

    [Fact]
    public void StorageOverflowIsLostAndAnUnroutedProducerCannotInventStorage()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        var catalog = PlanetaryIndustryTests.Catalog(f);
        var recipe = catalog.Schematics[PlanetaryIndustryTests.WaterSchematic];
        long now = f.Clock.Now;
        var filled = (int)
            Math.Floor(catalog.Pins[PlanetaryIndustryTests.CommandCenter].Capacity / catalog.Volumes[PlanetaryIndustryTests.Water]);
        PlanetaryPin command = new(1, PlanetaryIndustryTests.CommandCenter, 1, 1, now, [new(PlanetaryIndustryTests.Water, filled)]);
        PlanetaryPin processor = new(
            2,
            PlanetaryIndustryTests.Processor,
            1,
            1.001,
            now,
            [],
            recipe.Id,
            now + recipe.CycleSeconds * TimeSpan.TicksPerSecond
        );
        PlanetaryColony colony = new(
            f.PilotId,
            PlanetaryIndustryTests.PlanetId,
            0,
            now,
            10,
            10,
            [command, processor],
            [new(1, 2, 0)],
            [new(1, [2, 1], PlanetaryIndustryTests.Water, 20)]
        );
        var completed = PlanetarySimulation.Advance(colony, catalog, processor.NextRunAt!.Value);
        Assert.Equal(filled, Assert.Single(completed.Pins[0].Contents).Quantity);
        var unrouted = PlanetarySimulation.Advance(colony with { Routes = [] }, catalog, processor.NextRunAt.Value);
        Assert.Empty(unrouted.Pins[1].Contents);
    }

    [Fact]
    public void OverlappingExtractorQuotesAreIndependentOfInstallCommandOrder()
    {
        using var f = PlanetaryIndustryTests.Prepare();
        var catalog = PlanetaryIndustryTests.Catalog(f);
        var planet = new PlanetaryPlanet(
            PlanetaryIndustryTests.PlanetId,
            1,
            PlanetaryResources.Barren,
            1,
            6000000,
            SpacePosition.Zero,
            true
        );
        PlanetaryCommand[] layout =
        [
            new CreatePlanetaryPin(new(1), PlanetaryIndustryTests.CommandCenter, 1, 1),
            new UpgradePlanetaryCommand(new(1), 4),
            new CreatePlanetaryPin(new(1, 1), PlanetaryIndustryTests.Extractor, 1, 1.001),
            new CreatePlanetaryPin(new(2, 1), PlanetaryIndustryTests.Extractor, 1, 1.002),
            new AddPlanetaryHead(new(1, 1), new(0, 1, 1.001)),
            new AddPlanetaryHead(new(2, 1), new(0, 1, 1.002)),
        ];
        PlanetaryCommand first = new InstallPlanetaryProgram(new(1, 1), PlanetaryResources.AqueousLiquids, 0.01);
        PlanetaryCommand second = new InstallPlanetaryProgram(new(2, 1), PlanetaryResources.AqueousLiquids, 0.01);
        var initial = PlanetaryRules.Empty(f.PilotId, planet.Id, f.Clock.Now);
        var left = PlanetaryRules.Edit(initial, planet, catalog, [.. layout, first, second], 5, f.Clock.Now);
        var right = PlanetaryRules.Edit(initial, planet, catalog, [.. layout, second, first], 5, f.Clock.Now);
        Assert.Equal(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right));
    }
}
