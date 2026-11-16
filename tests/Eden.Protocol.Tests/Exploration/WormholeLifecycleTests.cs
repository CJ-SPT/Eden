using Eden.Data.World.Exploration;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Exploration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class WormholeLifecycleTests
{
    private const int Known = 30002813;
    private const int Remote = 31000356;

    [Fact]
    public void OversizedShipRejectionPreservesMassPositionAndPolarization()
    {
        using Fixture f = new();
        WormholeTravelTests.AddRoute(f);
        long ship = f.ShipId;
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1 WHERE ItemTypeId=30583 AND AttributeId=1385");
        }
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var travel = f.Db.Services.GetRequiredService<IWormholeTravel>();
        var source = Assert.Single(Assert.Single(travel.Signatures(Known)).Objects);
        f.Db.Store.SetPosition(ship, source.Position, Known);
        double remaining = live.Economy.Inspect(w => Assert.Single(w.Set<SavedWormholeConnection>()).RemainingMass);
        Assert.Throws<InvalidDataException>(() => travel.Prepare(f.PilotId, ship, source.Id));
        Assert.Throws<InvalidDataException>(() => travel.Transfer(f.PilotId, ship, source.Id, source.Id + 1));
        Assert.Equal(remaining, live.Economy.Inspect(w => Assert.Single(w.Set<SavedWormholeConnection>()).RemainingMass));
        Assert.Null(travel.Polarization(f.PilotId, Known, source.Id));
        Assert.Equal(Known, f.Db.Store.FindPilot(1000001)!.SystemId);
    }

    [Fact]
    public void PopulationPolicyProvidesClassAppropriateStaticsAndKnownSpaceWanderers()
    {
        using Fixture f = new();
        WormholeTravelTests.AddRoute(f);
        int[] systems = [31001001, 31001002, 31001003, 31001004, 31001005, 31001006, 30001007, 30001008, 30001009];
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            for (int i = 0; i < systems.Length; i++)
            {
                int system = systems[i];
                long celestial = 41000000 + i;
                string key = system.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string data = System.Text.Json.JsonSerializer.Serialize(new { wormholeClassID = i + 1 });
                catalog.Database.ExecuteSql($"INSERT INTO SolarSystem VALUES ({system},'Test system',20000020)");
                catalog.Database.ExecuteSql($"INSERT INTO SourceRecord VALUES ('client/systems',{key},{data})");
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z)
                    VALUES ({celestial},{system},52673,8,2000,0,0,0)
                    """
                );
            }
            (int Type, int Target)[] types =
            [
                (30666, 7),
                (30677, 7),
                (30686, 7),
                (30689, 1),
                (30693, 5),
                (30703, 6),
                (30711, 5),
                (30579, 1),
                (30584, 3),
                (30644, 5),
                (30645, 6),
                (30675, 5),
                (30674, 4),
            ];
            foreach (var (type, target) in types)
            {
                catalog.Database.ExecuteSql($"INSERT INTO ItemType(Id,Name,GroupId,Radius) VALUES ({type},'Test wormhole',988,3000)");
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT INTO TypeAttribute VALUES ({type},1381,{target}),({type},1382,960),
                        ({type},1383,750000000),({type},1384,0),({type},1385,375000000)
                    """
                );
            }
        }
        using CatalogDbContext source = new(f.CatalogPath);
        var authority = WormholeCatalog.Read(source);
        for (int i = 0; i < systems.Length; i++)
        {
            var slots = authority.Slots[systems[i]];
            Assert.Equal(i + 1 is 2 or 4 ? 2 : 1, slots.Length);
            foreach (var slot in slots)
            {
                int target = authority.Types[slot.Type].TargetClass;
                Assert.Contains(authority.Classes[target], s => s.Id != systems[i]);
                if (i + 1 is 1 or 3)
                {
                    Assert.InRange(target, 7, 9);
                }
                if (i + 1 >= 7)
                {
                    Assert.InRange(target, 1, 6);
                }
            }
        }
    }

    [Fact]
    public async Task FinalTransitCollapsesBothEndsAndReplacementRejectsOldIdentityAfterRestart()
    {
        using Fixture f = new();
        WormholeTravelTests.AddRoute(f);
        long ship = f.ShipId;
        long sourceId;
        long replacementId;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var travel = f.Db.Services.GetRequiredService<IWormholeTravel>();
            var source = Assert.Single(Assert.Single(travel.Signatures(Known)).Objects);
            sourceId = source.Id;
            f.Db.Store.SetPosition(ship, source.Position, Known);
            var exit = travel.Prepare(f.PilotId, ship, source.Id).Exit;
            using (var action = live.Economy.Begin())
            {
                var saved = Assert.Single(action.Set<SavedWormholeConnection>());
                saved.RemainingMass = 1;
                action.Commit();
            }
            Assert.Throws<InvalidDataException>(() => travel.Transfer(f.PilotId, ship, source.Id, source.Id));
            Assert.Equal(1, live.Economy.Inspect(w => Assert.Single(w.Set<SavedWormholeConnection>()).RemainingMass));
            Assert.Null(travel.Polarization(f.PilotId, Known, source.Id));
            Assert.Equal(Remote, travel.Transfer(f.PilotId, ship, source.Id, exit.Id).SystemId);
            Assert.Empty(travel.Signatures(Known));
            Assert.Empty(travel.Signatures(Remote));
            f.Db.Store.SetPosition(ship, exit.Position, Remote);
            Assert.Throws<InvalidDataException>(() => travel.Prepare(f.PilotId, ship, exit.Id));
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(WormholeRules.ReplacementSeconds));
            var replacement = Assert.Single(Assert.Single(travel.Signatures(Known)).Objects);
            replacementId = replacement.Id;
            Assert.NotEqual(source.Id, replacement.Id);
            Assert.NotEqual(source.Position, replacement.Position);
            Assert.Empty(travel.Signatures(Remote));
            f.Db.Store.SetPosition(ship, replacement.Position, Known);
            Assert.Throws<InvalidDataException>(() => travel.Prepare(f.PilotId, ship, source.Id));
            await live.FlushAsync();
        }
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var travel = f.Db.Services.GetRequiredService<IWormholeTravel>();
            var replacement = Assert.Single(Assert.Single(travel.Signatures(Known)).Objects);
            Assert.Equal(replacementId, replacement.Id);
            Assert.Throws<InvalidDataException>(() => travel.Prepare(f.PilotId, ship, sourceId));
            var jump = travel.Prepare(f.PilotId, ship, replacement.Id);
            Assert.Equal(Remote, jump.System.Id);
            using (var action = restarted.Economy.Begin())
            {
                Assert.Single(action.Set<SavedWormholeConnection>()).RemainingMass = 1;
                action.Commit();
            }
            travel.Transfer(f.PilotId, ship, replacement.Id, jump.Exit.Id);
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(WormholeRules.ReplacementSeconds));
            Assert.True(Assert.Single(travel.Signatures(Known)).Id > replacementId);
        }
    }

    [Fact]
    public async Task ActivationExpiryAndHiddenExitUseSavedDeadlinesAndWarmReadsStayResident()
    {
        using Fixture f = new();
        WormholeTravelTests.AddRoute(f);
        long ship = f.ShipId;
        long endpoint;
        long expiry;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var travel = f.Db.Services.GetRequiredService<IWormholeTravel>();
            var source = Assert.Single(Assert.Single(travel.Signatures(Known)).Objects);
            endpoint = source.Id;
            Assert.Empty(travel.Signatures(Remote));
            f.Clock.AdvanceWallTime(TimeSpan.FromDays(2));
            Assert.Equal(endpoint, Assert.Single(travel.Signatures(Known)).Id);
            travel.Activate(Known, endpoint);
            expiry = live.Economy.Inspect(w => Assert.Single(w.Set<SavedWormholeConnection>()).ExpiresAt);
            travel.Activate(Known, endpoint);
            Assert.Equal(expiry, live.Economy.Inspect(w => Assert.Single(w.Set<SavedWormholeConnection>()).ExpiresAt));
            f.Clock.AdvanceWallTime(TimeSpan.FromHours(2));
            Assert.Single(travel.Signatures(Remote));
            var warmed = travel.Signatures(Known);
            using (var measured = SessionPerformance.Begin("wormhole lifecycle warm reads", _ => { }, force: true)!)
            {
                Assert.Same(warmed, travel.Signatures(Known));
                Assert.Same(warmed, travel.Signatures(Known));
                Assert.Equal(0, measured.Commands);
                Assert.Equal(0, measured.Connections);
                Assert.Equal(0, measured.FittingCalculations);
            }
            await live.FlushAsync();
        }
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var travel = f.Db.Services.GetRequiredService<IWormholeTravel>();
            Assert.Equal(expiry, restarted.Economy.Inspect(w => Assert.Single(w.Set<SavedWormholeConnection>()).ExpiresAt));
            var source = Assert.Single(Assert.Single(travel.Signatures(Known)).Objects);
            f.Db.Store.SetPosition(ship, source.Position, Known);
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(expiry - f.Clock.Now - 1));
            Assert.Equal(Remote, travel.Prepare(f.PilotId, ship, endpoint).System.Id);
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
            Assert.Throws<InvalidDataException>(() => travel.Prepare(f.PilotId, ship, endpoint));
            Assert.Empty(travel.Signatures(Known));
            Assert.Empty(travel.Signatures(Remote));
        }
    }

    [Theory]
    [InlineData(100, 1)]
    [InlineData(51, 1)]
    [InlineData(50, .7)]
    [InlineData(11, .7)]
    [InlineData(10, .4)]
    public void StabilityBandsUseRemainingMassAndNativeMassCategories(double remaining, double expected)
    {
        Assert.Equal(expected, WormholeRules.Size(remaining, 100));
        var body = new StaticSpaceObject(WormholeRules.FirstEndpointId, 30583, WormholeRules.Group, 2, 1, SpacePosition.Zero, 3000)
        {
            Wormhole = new(Remote, 2, 375_000_000, 2, expected),
        };
        var slim = Assert.IsType<BlueInstance>(new StaticSlimItem(body).ToBlue());
        var fields = Assert.IsType<Dictionary<string, object?>>(slim.State);
        Assert.Equal(3, fields["maxShipJumpMass"]);
        Assert.Equal(2, fields["wormholeAge"]);
        Assert.Equal(expected, fields["wormholeSize"]);
    }

    [Fact]
    public void RegenerationCapsMassAndCannotReviveACollapsedHole()
    {
        Assert.Equal(75, WormholeRules.Regenerate(50, 100, 50, 1, 1 + TimeSpan.TicksPerDay / 2));
        Assert.Equal(100, WormholeRules.Regenerate(50, 100, 50, 1, 1 + TimeSpan.TicksPerDay * 2));
        Assert.Equal(0, WormholeRules.Regenerate(0, 100, 50, 1, 1 + TimeSpan.TicksPerDay));
        Assert.Equal(50, WormholeRules.Regenerate(50, 100, 50, 2, 1));
    }
}
