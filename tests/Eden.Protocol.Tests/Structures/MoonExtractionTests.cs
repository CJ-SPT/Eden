using System.Globalization;
using System.Text.Json;
using Eden.Data.World.Mining;
using Eden.Data.World.Structures;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Structures;

public sealed class MoonExtractionTests
{
    private const int Athanor = 35835;
    private const int StandupMoonDrill = 45009;
    private const int FuelBlock = 4051;
    private const int MoonId = 40000991;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MoonPointIsExclusiveAndSavedExtractionFracturesAndExpiresOnce(bool automatic)
    {
        using StructureTests.Fixture fixture = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath, writable: true))
        {
            const string system = """
                {"security":0.4,"planets":{"40000990":{"moons":{"40000991":{"miningBeacon":{"position":{"x":10000000000,"y":0,"z":0}}}}}}}
                """;
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30001392',{0})",
                system
            );
            catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO ItemCategory VALUES(25,'Asteroid')");
            catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO ItemGroup VALUES(1884,'Ubiquitous Moon Asteroids',25)");
            foreach (var share in MoonMiningRules.Composition(MoonId, 0.4))
            {
                catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES({0},'Moon ore',1884)", share.TypeId);
                catalog.Database.ExecuteSqlRaw(
                    "INSERT OR REPLACE INTO SourceRecord VALUES ('client/types',{0},{1})",
                    share.TypeId.ToString(CultureInfo.InvariantCulture),
                    JsonSerializer.Serialize(new { volume = 10, published = 1 })
                );
            }
        }

        var structure = fixture.Online(Athanor);
        Assert.Equal(MoonId, structure.MoonId);
        long duplicate = fixture.Item(Athanor);
        Assert.Throws<InvalidDataException>(() =>
            fixture.Structures.Deploy(fixture.PilotId, new(duplicate, StructureTests.Fixture.Point, 0, "Second refinery"))
        );
        long drill = fixture.Item(StandupMoonDrill);
        fixture.Structures.FitService(fixture.PilotId, structure.Id, drill, StructureRules.FirstServiceSlot);
        fixture.Structures.DepositFuel(fixture.PilotId, structure.Id, fixture.Item(FuelBlock, 1_000), 1_000);
        fixture.Structures.SetServiceOnline(fixture.PilotId, structure.Id, drill, true);
        var extractions = fixture.Db.Services.GetRequiredService<IMoonExtractions>();

        long seconds = MoonMiningRules.MinimumExtractionSeconds;
        Assert.Throws<InvalidDataException>(() => extractions.Start(fixture.PilotId, structure.Id, seconds - 1));
        var extraction = extractions.Start(fixture.PilotId, structure.Id, seconds);
        Assert.Equal(extraction, extractions.Current(fixture.PilotId, structure.Id));
        Assert.Throws<InvalidDataException>(() => extractions.Fracture(fixture.PilotId, structure.Id));
        WorldInitialization.Initialize(new(fixture.Db.Path), fixture.Clock);
        Assert.Equal(extraction, extractions.Current(fixture.PilotId, structure.Id));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(seconds));
        if (automatic)
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(MoonMiningRules.AutoFractureDelay));
            fixture.Structures.Advance();
        }
        else
        {
            extractions.Fracture(fixture.PilotId, structure.Id);
        }
        Assert.Null(extractions.Current(fixture.PilotId, structure.Id));
        Assert.Throws<InvalidDataException>(() => extractions.Fracture(fixture.PilotId, structure.Id));

        using (WorldDbContext saved = new(fixture.Db.Path))
        {
            Assert.True(saved.Set<SavedMoonExtraction>().Find(extraction.Id)!.Ended);
            var rocks = saved.Set<WorldAsteroid>().Where(rock => rock.BeltId == MoonId).ToArray();
            Assert.InRange(rocks.Length, 16, 32);
            Assert.Equal(432_000L, rocks.Sum(rock => rock.Quantity));
        }

        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(MoonMiningRules.FieldLifetime));
        fixture.Structures.Advance();
        using WorldDbContext expired = new(fixture.Db.Path);
        Assert.Empty(expired.Set<WorldAsteroid>().Where(rock => rock.BeltId == MoonId));
    }

    [Fact]
    public void CompositionIsStableAndEveryMoonOreHasAnEligibleMoon()
    {
        var compositions = Enumerable.Range(40_000_000, 1000).Select(id => MoonMiningRules.Composition(id, 0.3)).ToArray();
        Assert.All(compositions, shares => Assert.Equal(100, shares.Sum(share => share.Percentage)));
        Assert.Equal(20, compositions.SelectMany(shares => shares).Select(share => share.TypeId).Distinct().Count());
        Assert.Empty(MoonMiningRules.Composition(MoonId, 0.55));
        Assert.Equal(MoonMiningRules.Composition(MoonId, 0.3), MoonMiningRules.Composition(MoonId, 0.3));
    }
}
