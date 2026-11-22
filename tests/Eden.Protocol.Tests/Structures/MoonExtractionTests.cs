using System.Globalization;
using System.Text.Json;
using Eden.Data.World.Mining;
using Eden.Data.World.Structures;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Structures;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Client = Eden.Protocol.Tests.PilotSessions.CourierServiceTests.Client;

namespace Eden.Protocol.Tests.Structures;

public sealed class MoonExtractionTests
{
    private const int Athanor = 35835;
    private const int StandupMoonDrill = 45009;
    private const int FuelBlock = 4051;
    private const int MoonId = 40000991;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MoonPointIsExclusiveAndSavedExtractionFracturesAndExpiresOnce(bool automatic, bool resident)
    {
        using StructureTests.Fixture fixture = new(liveWorld: resident);
        SeedMoon(fixture);

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
        if (LiveWorld.Find(fixture.Db.Path) is { } live)
        {
            using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
            checkpoint.Project();
            using WorldDbContext reopened = new(checkpoint.Database);
            Assert.Equal(extraction.ReadyAt, reopened.Set<SavedMoonExtraction>().Find(extraction.Id)!.ReadyAt);
        }
        else
        {
            WorldInitialization.Initialize(new(fixture.Db.Path), fixture.Clock);
        }
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
    public void NativeMoonButtonsAdvanceOneSavedTimelineAndKeepTheExtractionYield()
    {
        using StructureTests.Fixture f = new(liveWorld: true);
        SeedMoon(f);
        var structure = f.Online(Athanor);
        long drill = f.Item(StandupMoonDrill);
        f.Structures.FitService(f.PilotId, structure.Id, drill, StructureRules.FirstServiceSlot);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(FuelBlock, 1_000), 1_000);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, drill, true);
        var store = f.Db.Services.GetRequiredService<IMoonExtractions>();
        var initial = store.Start(f.PilotId, structure.Id, MoonMiningRules.MinimumExtractionSeconds);
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        using Client player = new(f.Db, f.Clock, 1000001);
        player.Refresh();
        Assert.IsType<PilotCallResult.Failure>(player.Call("SlashCmd", ["/extraction advance mid"], "slash"));
        using Client gm = new(f.Db, f.Clock, 1000001, PilotRole.GameMasterLow);
        gm.Refresh();

        long duration = initial.ReadyAt - initial.StartedAt;
        long now = f.Clock.Now;
        Assert.Equal(PilotCallResult.None, gm.Call("SlashCmd", ["/extraction advance mid"], "slash"));
        Assert.Equal(now + duration / 2, store.Current(f.PilotId, structure.Id)!.ReadyAt);
        Assert.Equal(PilotCallResult.None, gm.Call("SlashCmd", ["/extraction advance stop"], "slash"));
        Assert.Equal(now + TimeSpan.TicksPerMinute, store.Current(f.PilotId, structure.Id)!.ReadyAt);
        Assert.Equal(PilotCallResult.None, gm.Call("SlashCmd", ["/extraction advance ready"], "slash"));
        Assert.Equal(now, store.Current(f.PilotId, structure.Id)!.ReadyAt);
        Assert.Equal(PilotCallResult.None, gm.Call("SlashCmd", ["/extraction advance decay"], "slash"));
        var accelerated = store.Current(f.PilotId, structure.Id)!;
        Assert.Equal(now + TimeSpan.FromSeconds(5).Ticks, accelerated.AutoFractureAt);
        Assert.Equal(duration, accelerated.ReadyAt - accelerated.StartedAt);

        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(5));
        f.Structures.Advance();
        Assert.Null(store.Current(f.PilotId, structure.Id));
        var live = LiveWorld.Find(f.Db.Path)!;
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        Assert.Equal(432_000L, saved.Set<WorldAsteroid>().Where(rock => rock.BeltId == MoonId).Sum(rock => rock.Quantity));
    }

    private static void SeedMoon(StructureTests.Fixture fixture)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath, writable: true);
        const string system = """
            {"security":0.4,"planets":{"40000990":{"moons":{"40000991":{"miningBeacon":{"position":{"x":10000000000,"y":0,"z":0}}}}}}}
            """;
        catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30001392',{0})", system);
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
