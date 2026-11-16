using System.Text.Json;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Exploration;
using Eden.World.Loot;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class SleeperSiteTests
{
    [Fact]
    public async Task BlueLootUsesAuthorizedNpcBidsAndResidentInventoryWalletSettlement()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadWormholes(f.CatalogPath);
        long item = f.Grant(30744, 2);
        int station = checked((int)f.StationId);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            string data = "{\"corporationTrades\":{\"30744\":-1,\"30745\":-1,\"30746\":-1,\"30747\":-1}}";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/npccorporations','1000035',{data})");
            string operation = "{\"services\":[7]}";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14',{operation})");
        }
        var configuration = new Eden.World.Configuration.WorldConfigurationStore(new(f.Db.Path), f.Clock);
        var state = configuration.Read();
        configuration.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with
                {
                    DevelopmentSupply = state.Active.Draft.Settings.DevelopmentSupply with { TestingMarket = false },
                },
            },
            "test startup"
        );
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        long balance;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            Assert.False(
                live.Economy.Inspect(world => world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket)
            );
            using CatalogDbContext catalog = new(f.CatalogPath);
            Assert.Equal(1000035, catalog.RequireStation(station).OwnerId);
            Assert.Equal(4, catalog.Read(CatalogData.Client.Npccorporations, 1000035L)!.CorporationTrades!.Count);
            var market = f.Db.Services.GetRequiredService<IPilotMarket>();
            var bids = market.Orders(f.PilotId).Where(o => o.Bid && o.StationId == station).ToArray();
            (int Type, long Price)[] prices = [(30744, 20_000_000), (30745, 50_000_000), (30746, 150_000_000), (30747, 500_000_000)];
            foreach (var (type, expected) in prices)
            {
                Assert.Contains(bids, order => order.TypeId == type && order.PriceCents == expected);
            }
            const long price = 20_000_000;
            long before = market.Balance(f.PilotId);
            Assert.Throws<InvalidDataException>(() => market.Sell(f.PilotId, [new(item, station, 30744, price + 1, 2)]));
            Assert.Equal(before, market.Balance(f.PilotId));
            Assert.Equal(2, live.Economy.Inspect(world => world.Set<OwnedItem>().Find(item)!.Quantity));
            var result = market.Sell(f.PilotId, [new(item, station, 30744, price, 2)]);
            var trade = Assert.Single(result.Trades);
            Assert.Equal(price, trade.PriceCents);
            Assert.Equal(2, trade.Quantity);
            balance = before + price * 2 - trade.TaxCents;
            Assert.Equal(balance, result.BalanceCents);
            Assert.Null(live.Economy.Inspect(world => world.Set<OwnedItem>().Find(item)));
            await live.FlushAsync();
        }
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            Assert.Equal(balance, f.Db.Services.GetRequiredService<IPilotMarket>().Balance(f.PilotId));
            Assert.Null(restarted.Economy.Inspect(world => world.Set<OwnedItem>().Find(item)));
        }
    }

    [Fact]
    public void AllTwentyFourCombatReportsBindNativeGeometryAndNpcProfiles()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadWormholes(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var definitions = SleeperSiteCatalog.Read(catalog).Where(d => d.WormholeKind == WormholeSiteKind.Combat).ToArray();
        Assert.Equal(24, definitions.Length);
        for (int level = 1; level <= 6; level++)
        {
            Assert.Equal(4, definitions.Count(d => d.WormholeClass == level));
        }
        foreach (var definition in definitions)
        {
            Assert.NotEmpty(definition.SpawnAnchors);
            Assert.NotEmpty(definition.Waves);
            foreach (int type in definition.Waves.SelectMany(w => w.Groups).SelectMany(g => g.Types).Distinct())
            {
                var profile = new NpcProfileCatalog().Read(catalog, type);
                Assert.True(profile.Defense.Structure > 0);
                Assert.NotNull(SleeperLootCatalog.Drops(type));
            }
        }
        var ambush = definitions.Single(d => d.Name == "Perimeter Ambush Point");
        long blueLoot = ambush
            .Waves.SelectMany(w => w.Groups)
            .Sum(group =>
                group.Minimum
                * SleeperLootCatalog
                    .Drops(group.Types.Single())!
                    .Sum(drop => drop.Quantity * SleeperRules.BlueLootPriceCents(drop.TypeId)!.Value)
            );
        Assert.Equal(860_000_000, blueLoot); // The documented 8.6 million ISK roster, before tax and salvage.
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ResidentClassSitesAdvanceWavesProduceLootAndRecoverAfterCheckpoint(int level)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadWormholes(f.CatalogPath);
        long ship = f.ShipId;
        int system;
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            system = checked((int)catalog.Records(CatalogData.Client.Systems).First(s => s.Value.WormholeClassID == level).Key);
        }
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            long celestial = 50_000_000 + level;
            catalog.Database.ExecuteSql(
                $"""
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z)
                VALUES ({celestial},{system},52673,8,2000,0,0,0)
                """
            );
        }
        long siteId;
        int wrecks;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            using CatalogDbContext catalog = new(f.CatalogPath);
            CombatAnomalyStore sites = new(new(f.Db.Path));
            var current = sites.List(system, f.Clock.Now);
            Assert.Equal(4, current.Count);
            var site = current[0];
            siteId = site.Id;
            Assert.All(current, c => Assert.Equal(level, CombatAnomalyCatalog.Find(catalog, c.DungeonId)!.WormholeClass));
            f.Db.Store.SetPosition(ship, site.Position, system);
            sites.Arrive(system, site.Position, f.Clock.Now);
            var definition = CombatAnomalyCatalog.Find(catalog, site.DungeonId)!;
            var first = live.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == site.Id).ToArray());
            Assert.NotEmpty(first);
            Assert.All(first, npc => Assert.Equal(SleeperRules.FactionId, npc.FactionId));
            var warmed = sites.List(system, f.Clock.Now);
            using (var measured = SessionPerformance.Begin("resident Sleeper views", _ => { }, force: true)!)
            {
                Assert.Same(warmed, sites.List(system, f.Clock.Now));
                Assert.Equal(0, measured.Commands);
                Assert.Equal(0, measured.Connections);
                Assert.Equal(0, measured.FittingCalculations);
            }
            int steps = 0;
            while (!live.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(site.Id)!.CompletedAt.HasValue))
            {
                Assert.True(steps++ <= definition.Waves.Length);
                using var action = live.Economy.Begin(new(systems: [system], pilots: [f.PilotId]));
                var pilot = action.Pilots.Find(f.PilotId)!;
                var alive = action.Filter<WorldNpc>(n => n.EncounterId == site.Id && n.DestroyedAt == null).ToArray();
                Assert.NotEmpty(alive);
                foreach (var npc in alive)
                {
                    var attributes = new NpcProfileCatalog().Read(catalog, npc.TypeId).Attributes;
                    var killed = NpcDamagePersistence.Apply(
                        action,
                        catalog,
                        npc,
                        pilot,
                        ship,
                        attributes,
                        new(1_000_000_000, 0, 0, 0),
                        f.Clock.Now,
                        bypassResistances: true
                    );
                    Assert.True(killed.Destroyed);
                    Assert.Null(killed.Reward);
                }
                action.Commit();
            }
            Assert.DoesNotContain(sites.List(system, f.Clock.Now), s => s.Id == site.Id);
            wrecks = live.Economy.Inspect(world =>
            {
                var rows = world.Filter<WorldWreck>(w => w.SystemId == system).ToArray();
                Assert.NotEmpty(rows);
                foreach (var wreck in rows)
                {
                    Assert.NotNull(wreck.SalvageJson);
                    var salvage = JsonSerializer.Deserialize<ScanItem[]>(wreck.SalvageJson!)!;
                    Assert.All(
                        salvage,
                        item => Assert.Contains((SalvageMaterial)item.TypeId, SalvageLootRules.Pool(SalvageFamily.Sleeper))
                    );
                    var expected = SleeperLootCatalog.Drops(world.Set<WorldNpc>().Find(wreck.NpcId!.Value)!.TypeId)!;
                    var loot = world.Filter<OwnedItem>(item => item.LocationId == wreck.Id).OrderBy(item => item.TypeId).ToArray();
                    Assert.Equal(
                        expected.OrderBy(drop => drop.TypeId).Select(drop => (drop.TypeId, drop.Quantity)),
                        loot.Select(item => (item.TypeId, item.Quantity))
                    );
                }
                return rows.Length;
            });
            await live.FlushAsync();
        }
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            Assert.True(restarted.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(siteId)!.CompletedAt.HasValue));
            Assert.Equal(wrecks, restarted.Economy.Inspect(world => world.Filter<WorldWreck>(w => w.SystemId == system).Count()));
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(CombatAnomalyRules.ReplacementSeconds));
            var replaced = new CombatAnomalyStore(new(f.Db.Path)).List(system, f.Clock.Now);
            Assert.Equal(4, replaced.Count);
            Assert.DoesNotContain(replaced, s => s.Id == siteId);
        }
    }
}
