using Eden.Data.World.Characters;
using Eden.Data.World.Docking;
using Eden.Data.World.Economy;
using Eden.Data.World.FactionWarfare;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Alliances;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.FactionWarfare;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed class MilitiaProgressionTests
{
    private const int SystemId = 30000142;
    private const int HullType = 587;
    private const int CargoType = 34;

    [Fact]
    public void DirectEnlistmentCountsThePilotWithoutEnlistingTheirCorporationInStatistics()
    {
        const MilitiaFaction faction = MilitiaFaction.Minmatar;
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        long direct = fixture.Store.FindPilot(1000001)!.PilotId;
        long organized = fixture.Store.FindPilot(1000002)!.PilotId;
        AllianceCreationTests.Corporation(fixture, direct);
        int enlistedCorporation = AllianceCreationTests.Corporation(fixture, organized, "Enlisted Militia Corp", "WARC");
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(new(pilots: [direct, organized])))
        {
            SetEnlistment(action, direct, faction);
            action.Add(
                new SavedMilitiaOrganization
                {
                    OwnerId = enlistedCorporation,
                    Kind = MilitiaOrganizationKind.Corporation,
                    Phase = MilitiaOrganizationPhase.Active,
                    Faction = faction,
                    EffectiveAt = clock.Now,
                }
            );
            action.Commit();
        }

        var progression = fixture.Services.GetRequiredService<IMilitiaProgression>();
        var statistics = Assert.Single(progression.Factions(), row => row.Faction == faction);
        Assert.Equal(2, statistics.TotalMembersCount);
        Assert.Equal(0, statistics.MilitiaMembersCount);
        Assert.Equal(1, statistics.CorporationsCount);
        Assert.Equal(enlistedCorporation, statistics.TopCorporation);
        Assert.Equal(1, statistics.TopMemberCount);
    }

    [Fact]
    public async Task BiomassDropsMilitiaHeadcountsAndKeepsHistoricalActivityAcrossCheckpoint()
    {
        const MilitiaFaction faction = MilitiaFaction.Minmatar;
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var current = fixture.Store.FindPilot(1000001)!;
        var departing = fixture.Store.FindPilot(1000002)!;
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(new(pilots: [current.PilotId, departing.PilotId])))
        {
            int corporation = MilitiaRules.Corporation(faction);
            action.Pilots.Find(current.PilotId)!.CorporationId = corporation;
            var pilot = action.Pilots.Find(departing.PilotId)!;
            pilot.CorporationId = corporation;
            MilitiaProgressionStore.Record(action, pilot, faction, new(1, 25), clock.Now);
            action.Commit();
        }
        AssertMembership(fixture.Services, 2);
        var deletion = fixture.Services.GetRequiredService<ICharacterDeletion>();
        long ready = deletion.Prepare(1000002, departing.PilotId);
        clock.AdvanceWallTime(TimeSpan.FromTicks(ready - clock.Now));
        deletion.Delete(1000002, departing.PilotId);
        AssertMembership(fixture.Services, 1);
        await live.FlushAsync();
        fixture.Services.Dispose();
        using var reopened = ServerServices.Build(new(fixture.Path), clock, liveWorld: true);
        AssertMembership(reopened, 1);

        void AssertMembership(IServiceProvider services, int members)
        {
            var progression = services.GetRequiredService<IMilitiaProgression>();
            var statistics = Assert.Single(progression.Factions(), row => row.Faction == faction);
            Assert.Equal(members, statistics.TotalMembersCount);
            Assert.Equal(members, statistics.MilitiaMembersCount);
            Assert.Equal(members, progression.CorporationPilots(current.PilotId));
            Assert.Equal(new MilitiaActivity(1, 25), statistics.Activity.Total);
        }
    }

    [Fact]
    public async Task ShipLossAwardsOnlyDestroyedValueAndPersistsNativeStatistics()
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));
        using TestWorld fixture = new(clock: clock);
        var database = new WorldDatabase(fixture.Path);
        LoadCombatCatalog(database);
        using var host = ServerServices.Build(database, clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        fixture.Store.SetPosition(fixture.Store.FindPilot(1000002)!.ShipId, new(0, 0, 0), SystemId);
        long killer = fixture.Store.FindPilot(1000001)!.PilotId;
        long victim = fixture.Store.FindPilot(1000002)!.PilotId;
        int issuer = MilitiaRules.Corporation(MilitiaFaction.Minmatar);
        long hull = Destroy(MilitiaFaction.Minmatar, MilitiaFaction.Amarr);
        Assert.Equal(
            (100L, hull, MilitiaLoyaltySource.PlayerShipLoss),
            live.Economy.Inspect(world =>
            {
                var row = Assert.Single(world.Set<SavedMilitiaLoyaltyAward>());
                Assert.Equal(killer, row.PilotId);
                Assert.Equal(issuer, row.IssuerId);
                Assert.Equal(100, world.Set<SavedLoyaltyBalance>().Find(killer, issuer)!.Points);
                return (row.Points, row.SourceId, row.Source);
            })
        );
        Destroy(MilitiaFaction.Minmatar, MilitiaFaction.Minmatar);
        Destroy(MilitiaFaction.Minmatar, null);
        Destroy(MilitiaFaction.Minmatar, MilitiaFaction.Amarr, sameAccount: true);
        Assert.Single(live.Economy.Inspect(world => world.Set<SavedMilitiaLoyaltyAward>().ToArray()));

        clock.UtcNow = clock.UtcNow.AddDays(1);
        AssertReplies(host);
        await live.FlushAsync();
        host.Dispose();
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        AssertReplies(reopened);
        Assert.Equal(
            100,
            reopened.GetRequiredService<LiveWorld>().Economy.Inspect(world => Assert.Single(world.Set<SavedMilitiaLoyaltyAward>()).Points)
        );
        clock.UtcNow = clock.UtcNow.AddDays(7);
        var stats = reopened.GetRequiredService<IMilitiaProgression>().Statistics(killer, MilitiaStatisticOwner.Pilot);
        Assert.Equal(new(default, default, new(1, 0)), stats);

        long Destroy(MilitiaFaction attackerFaction, MilitiaFaction? defenderFaction, bool sameAccount = false)
        {
            using var action = live.Economy.Begin(new(pilots: [killer, victim]));
            SetEnlistment(action, killer, attackerFaction);
            SetEnlistment(action, victim, defenderFaction);
            var attacker = action.Pilots.Find(killer)!;
            var defender = action.Pilots.Find(victim)!;
            long account = defender.AccountId;
            defender.AccountId = sameAccount ? attacker.AccountId : account;
            var ship = defender.ActiveShip;
            ship.TypeId = HullType;
            var position = ship.Position!;
            action.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(action),
                    OwnerId = victim,
                    LocationId = ship.Id,
                    FlagId = (int)InventoryFlag.RigSlot0,
                    TypeId = CargoType,
                    Quantity = 10,
                }
            );
            action.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(action),
                    OwnerId = victim,
                    LocationId = ship.Id,
                    FlagId = (int)InventoryFlag.Cargo,
                    TypeId = CargoType,
                    Quantity = 50,
                }
            );
            if (action.Set<SavedAdjustedPrice>().Find(CargoType) is null)
            {
                action.Add(new SavedAdjustedPrice { TypeId = CargoType, PriceIsk = 100_000 });
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            ShipLossPersistence.Apply(
                action,
                catalog,
                defender,
                null,
                ship.Id,
                SystemId,
                position,
                clock.Now,
                new KeepCargo(),
                PilotInteractionHistoryStore.Player(action, attacker, attacker.ActiveShipId)
            );
            defender.AccountId = account;
            action.Commit();
            return ship.Id;
        }

        void AssertReplies(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var pilot = fixture.Store.FindPilot(1000001)!;
            using PilotCallContext context = new(
                1000001,
                pilot.Account,
                [pilot],
                current => new(fixture.Store, current, clock, new(), []),
                42,
                SignedSettings.Empty
            );
            context.Select(pilot);
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            var personal = BlueRead.Fields(Reply("GetStats_Character"));
            var corporation = BlueRead.Fields(Reply("GetStats_Corp"));
            var alliance = BlueRead.Fields(Reply("GetStats_Alliance"));
            Assert.Equal(1L, personal["killsY"]);
            Assert.Equal(1L, personal["killsLW"]);
            Assert.Equal(1L, personal["killsTotal"]);
            Assert.Equal(0L, personal["vpTotal"]);
            Assert.Equal(1L, corporation["killsTotal"]);
            Assert.Equal(0L, alliance["killsTotal"]);
            var table = BlueRead.Fields(Reply("GetStats_Militia"));
            var data = BlueRead.Fields(table["data"]);
            Assert.Equal(1L, Group(data["killsTotal"])[(long)MilitiaFaction.Minmatar]);
            Assert.Equal(4, Assert.IsType<List<object?>>(table["header"]).Count);
            var scores = BlueRead.Tuple(Reply("GetStats_TopAndAllKillsAndVPs"), 2);
            Assert.Equal(1L, BlueRead.Fields(Group(scores[0])[1])["killsTotal"]);
            Assert.Equal(1L, BlueRead.Fields(Group(scores[1])[2])["killsTotal"]);

            object? Reply(string method)
            {
                var response = Assert.IsType<PilotCallResult.Success>(
                    router.Call(context, "facWarMgr", method, Array.Empty<object?>(), _ => { })
                );
                return BlueMarshal.Decode(BlueMarshal.Encode(response.Reply));
            }

            static Dictionary<long, object?> Group(object? value) => Assert.IsType<Dictionary<long, object?>>(value);
        }
    }

    [Fact]
    public async Task AutonomousMilitiaKillsKeepReportsAndStatsWithoutPayingLoyaltyPoints()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock);
        WorldDatabase database = new(fixture.Path);
        LoadCombatCatalog(database);
        using var host = ServerServices.Build(database, clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        var human = fixture.Store.FindPilot(1000001)!;
        var opponent = fixture.Store.FindPilot(1000002)!;
        fixture.Store.SetPosition(human.ShipId, SpacePosition.Zero, SystemId);
        fixture.Store.SetPosition(opponent.ShipId, SpacePosition.Zero, SystemId);
        long alliedBot;
        long opposingBot;
        using (var action = live.Economy.Begin(new(pilots: [human.PilotId, opponent.PilotId])))
        {
            if (action.Set<Account>().Find(CapsuleerCommunityStore.MinerAccountId) is null)
            {
                action.Add(
                    new Account
                    {
                        Id = CapsuleerCommunityStore.MinerAccountId,
                        UserName = "Capsuleer Community",
                        CreatedAt = clock.Now,
                        CloneGrade = AccountCloneGrade.Omega,
                    }
                );
            }
            SetEnlistment(action, human.PilotId, MilitiaFaction.Minmatar);
            SetEnlistment(action, opponent.PilotId, MilitiaFaction.Amarr);
            long first = action.Pilots.Select(pilot => pilot.Id).Max() + 1;
            alliedBot = AddBot(action, first, MilitiaFaction.Minmatar, 20);
            opposingBot = AddBot(action, first + 1, MilitiaFaction.Amarr, 21);
            action.Add(new SavedAdjustedPrice { TypeId = CargoType, PriceIsk = 100_000 });
            action.Commit();
        }

        long humanAgainstBot = Destroy(human.PilotId, opposingBot);
        long botAgainstHuman = Destroy(alliedBot, opponent.PilotId);
        long botAgainstBot = Destroy(opposingBot, alliedBot);
        long humanAgainstHuman = Destroy(human.PilotId, opponent.PilotId, alliedBot);
        AssertResults(host);
        await live.FlushAsync();
        host.Dispose();
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        AssertResults(reopened);

        void AssertResults(IServiceProvider services)
        {
            var progression = services.GetRequiredService<IMilitiaProgression>();
            Assert.Equal(2, progression.Statistics(human.PilotId, MilitiaStatisticOwner.Pilot).Total.Kills);
            Assert.Equal(1, progression.Statistics(alliedBot, MilitiaStatisticOwner.Pilot).Total.Kills);
            Assert.Equal(1, progression.Statistics(opposingBot, MilitiaStatisticOwner.Pilot).Total.Kills);
            Assert.Equal(3, progression.Statistics(human.PilotId, MilitiaStatisticOwner.Faction).Total.Kills);
            var losses = services
                .GetRequiredService<LiveWorld>()
                .Economy.Inspect(world =>
                {
                    var award = Assert.Single(world.Set<SavedMilitiaLoyaltyAward>());
                    Assert.Equal((human.PilotId, humanAgainstHuman, 100L), (award.PilotId, award.SourceId, award.Points));
                    return world.Filter<SavedCombatReport>(report => report.IsLoss).ToArray().ToDictionary(report => report.SourceKey);
                });
            Assert.Equal(human.PilotId, losses[$"ship:{humanAgainstBot}"].Snapshot().FinalBlow.CharacterId);
            Assert.Equal(alliedBot, losses[$"ship:{botAgainstHuman}"].Snapshot().FinalBlow.CharacterId);
            Assert.Equal(opposingBot, losses[$"ship:{botAgainstBot}"].Snapshot().FinalBlow.CharacterId);
            var shared = losses[$"ship:{humanAgainstHuman}"].Snapshot();
            Assert.Equal(alliedBot, Assert.Single(shared.OtherAttackers).CharacterId);
        }

        long AddBot(WorldAction action, long id, MilitiaFaction faction, int slot)
        {
            var template = action.Pilots.Find(human.PilotId)!;
            long ship = InventoryPersistence.NextId(action);
            int station = (int)human.Home.Id;
            string name = $"Test militia pilot {slot}";
            action.Add(new InventoryOwner { Id = id });
            action.Add(
                new Ship
                {
                    Id = ship,
                    OwnerId = id,
                    StationId = station,
                    TypeId = HullType,
                    Name = name,
                }
            );
            action.Add(new FlightPosition { ShipId = ship, SystemId = SystemId });
            action.Add(
                new Pilot
                {
                    Id = id,
                    AccountId = CapsuleerCommunityStore.MinerAccountId,
                    ActiveShipId = ship,
                    CorporationId = template.CorporationId,
                    CreatedAt = clock.Now,
                    Name = name,
                    RaceId = template.RaceId,
                    BloodlineId = template.BloodlineId,
                    GenderId = template.GenderId,
                    SchoolId = template.SchoolId,
                    AncestryId = template.AncestryId,
                }
            );
            MilitiaComplexTests.RegisterCapsuleer(action, id, human.HomeSystemId, station, slot);
            SetEnlistment(action, id, faction);
            return id;
        }

        long Destroy(long finalId, long victimId, long? assist = null)
        {
            using var action = live.Economy.Begin(new(pilots: [finalId, victimId]));
            var final = action.Pilots.Find(finalId)!;
            var victim = action.Pilots.Find(victimId)!;
            final.ActiveShip.TypeId = HullType;
            var hull = victim.ActiveShip;
            hull.TypeId = HullType;
            action.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(action),
                    OwnerId = victimId,
                    LocationId = hull.Id,
                    FlagId = (int)InventoryFlag.RigSlot0,
                    TypeId = CargoType,
                    Quantity = 10,
                }
            );
            if (assist is { } id)
            {
                var attacker = action.Pilots.Find(id)!;
                attacker.ActiveShip.TypeId = HullType;
                PilotInteractionHistoryStore.Damage(
                    action,
                    hull.Id,
                    PilotInteractionHistoryStore.Player(action, attacker, attacker.ActiveShipId),
                    1
                );
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            ShipLossPersistence.Apply(
                action,
                catalog,
                victim,
                null,
                hull.Id,
                SystemId,
                hull.Position!,
                clock.Now,
                new KeepCargo(),
                PilotInteractionHistoryStore.Player(action, final, final.ActiveShipId)
            );
            action.Commit();
            return hull.Id;
        }
    }

    [Fact]
    public async Task PromotionsSurviveRetirementAndDoNotPayAgainAfterDemotion()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        long pilot = fixture.Store.FindPilot(1000001)!.PilotId;
        const MilitiaFaction faction = MilitiaFaction.Minmatar;
        int militia = MilitiaRules.Corporation(faction);
        using (var action = live.Economy.Begin(new(pilots: [pilot])))
        {
            SetEnlistment(action, pilot, faction);
            action.Add(
                new SavedNpcStanding
                {
                    PilotId = pilot,
                    SourceId = militia,
                    Value = 0.99,
                }
            );
            Adjust(action, 0.002);
            action.Commit();
        }
        var progression = fixture.Services.GetRequiredService<IMilitiaProgression>();
        Assert.Equal(1, progression.Rank(pilot)!.Rank);
        using (var scope = fixture.Services.CreateScope())
        {
            var current = fixture.Store.FindPilot(1000001)!;
            using PilotCallContext context = new(
                1000001,
                current.Account,
                [current],
                selected => new(fixture.Store, selected, clock, new(), []),
                42,
                SignedSettings.Empty
            );
            context.Select(current);
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            Assert.Equal(1L, Reply("CheckForRankChange"));
            var rank = Assert.IsType<BlueInstance>(Reply("GetMyCharacterRankInfo"));
            Assert.Equal("utillib.KeyVal", rank.Name);
            var fields = BlueRead.Fields(rank.State);
            Assert.Equal(1L, fields["currentRank"]);
            Assert.Equal((long)faction, fields["factionID"]);
            Assert.Equal(pilot, fields["characterID"]);

            object? Reply(string method)
            {
                var response = Assert.IsType<PilotCallResult.Success>(
                    router.Call(context, "facWarMgr", method, Array.Empty<object?>(), _ => { })
                );
                return BlueMarshal.Decode(BlueMarshal.Encode(response.Reply));
            }
        }
        using (var action = live.Economy.Begin(new(pilots: [pilot])))
        {
            Adjust(action, -0.01);
            action.Commit();
        }
        Assert.Equal(0, progression.Rank(pilot)!.Rank);
        await live.FlushAsync();
        fixture.Services.Dispose();
        using var reopened = ServerServices.Build(new(fixture.Path), clock, liveWorld: true);
        live = reopened.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(new(pilots: [pilot])))
        {
            Adjust(action, 0.02);
            SetEnlistment(action, pilot, null);
            action.Commit();
        }
        progression = reopened.GetRequiredService<IMilitiaProgression>();
        Assert.Null(progression.Rank(pilot));
        Assert.Equal(new(pilot, faction, 1), Assert.Single(progression.Ranks(pilot)));
        live.Economy.Inspect(world =>
        {
            Assert.Equal(1, world.Set<SavedMilitiaRank>().Find(pilot, (int)faction)!.HighestRank);
            var award = Assert.Single(world.Set<SavedStandingTransaction>(), row => row.Event == StandingEvent.PromotionFactionIncrease);
            Assert.Equal((long)faction, award.SourceId);
            Assert.Equal(0.2, award.After, 8);
            Assert.Equal(0, MilitiaProgressionStore.Standing(world, pilot, (long)MilitiaFaction.Amarr));
            return true;
        });

        void Adjust(WorldAction action, double fraction) =>
            MilitiaProgressionStore.AdjustStanding(
                action,
                action.Pilots.Find(pilot)!,
                faction,
                fraction,
                StandingEvent.TacticalSiteConquered,
                SystemId,
                clock.Now
            );
    }

    [Fact]
    public void DestructionValuationAndShareConserveIntegerPoints()
    {
        Assert.Equal(0, MilitiaProgressionRules.PvpLoyalty(500, 1_000, 1_000));
        Assert.Equal(2, MilitiaProgressionRules.PvpLoyalty(18_000, 8_000, 19_999));
        Assert.Equal(50_000, MilitiaProgressionRules.PvpLoyalty(1_000_000_000, 0, 0));
        Assert.Equal(new long[] { 34, 33, 33 }, Enumerable.Range(0, 3).Select(i => MilitiaProgressionRules.Share(100, i, 3)));
    }

    private static void LoadCombatCatalog(WorldDatabase database)
    {
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (2,'Celestial'),(4,'Material');
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES
                (25,'Frigate',6),(29,'Capsule',6),(18,'Mineral',4),(186,'Wreck',2);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES
                (587,'Rifter',25),(670,'Capsule',29),(34,'Tritanium',18),(26973,'Wreck',186);
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','587','{{"wreckTypeID":26973}}');
            """
        );
    }

    private static void SetEnlistment(WorldAction action, long pilot, MilitiaFaction? faction)
    {
        var enlistment =
            action.Set<SavedMilitiaEnlistment>().Find(pilot) ?? action.Add(new SavedMilitiaEnlistment { PilotId = pilot }).Entity;
        enlistment.DirectFaction = faction;
    }

    private sealed class KeepCargo : IShipLootRoll
    {
        public bool Survives() => true;
    }
}
