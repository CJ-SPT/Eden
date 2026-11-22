using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Fact]
    public async Task EstablishedPilotBuysItsFirstPvpFitFromBountiesAndKeepsItAfterReopen()
    {
        const int merlin = (int)TechOneHullType.Merlin;
        const int railgun = (int)CombatWeaponType.GatlingRailgun75mm;
        const int scrambler = 447;
        const int charge = CapsuleerAmmunitionSupplyRules.ChargeType;
        const int pirate = 30380;
        const long priceCents = MarketDefinitions.CentsPerIsk;
        const long openingCents = 100_000 * MarketDefinitions.CentsPerIsk;
        const long bountyIsk = 2_000_000;

        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        PassiveCatalogFixture.LoadTackleTiers(fixture.Database.CatalogPath, new HashSet<int> { scrambler });
        PassiveCatalogFixture.LoadStarterMarket(
            fixture.Database.CatalogPath,
            new HashSet<int> { 3426, 3435, 3449, (int)MarketSkill.Trade }
        );
        using (CatalogDbContext edit = new(fixture.Database.CatalogPath, writable: true))
        {
            edit.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory VALUES (11,'Entity');
                INSERT OR IGNORE INTO ItemGroup VALUES (286,'Minor Threat',11),(186,'Wreck',2);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (30380,'Test Pirate',286),(26557,'Frigate Wreck',186);
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','30380','{{"wreckTypeID":26557}}'),
                    ('client/types','26557','{{"volume":27500}}'),('client/systems','30000141','{{"securityStatus":-0.2}}');
                UPDATE SourceRecord SET Data=json_set(Data,'$.basePrice',20)
                    WHERE Dataset='client/types' AND Key IN ('215','447');
                UPDATE SourceRecord SET Data=json_set(Data,'$.security',-0.2)
                    WHERE Dataset='client/solarsystemcontent' AND Key='30000141';
                """
            );
        }
        var config = new WorldConfigurationStore(fixture.Database, fixture.Clock);
        var initial = config.Read();
        var draft = initial.Active.Draft;
        var settings = draft.Settings;
        config.Save(
            initial.Generation,
            draft with
            {
                Settings = settings with
                {
                    Capsuleers = settings.Capsuleers with
                    {
                        EarnedPvpShips = true,
                        Exploration = false,
                        GateCamps = false,
                        RoamingGangs = false,
                        RoamPercent = 100,
                    },
                    Economy = settings.Economy with { ProgressiveMarkets = false, StandardMarket = false },
                    DevelopmentSupply = settings.DevelopmentSupply with { TestingMarket = false, TestingGrants = false },
                },
            },
            "Earned PvP ships"
        );
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        long pilotId;
        long workShipId;
        long paidShipId;
        long walletAfterPurchase;
        long[] purchaseIds;
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var ships = host.GetRequiredService<SqliteWorld>();
            var supply = host.GetRequiredService<CapsuleerIndustry>();
            MarketSale[] offers;
            using (var setup = live.Economy.Begin())
            {
                var agent = setup.Set<SavedCapsuleerAgent>().First(row => row.Career == CapsuleerCareer.Hauling);
                pilotId = agent.PilotId;
                foreach (var other in setup.Set<SavedCapsuleerAgent>().Where(row => row.PilotId != pilotId).ToArray())
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = other.PilotId });
                }
                var pilot = setup.Pilots.Find(pilotId)!;
                workShipId = pilot.ActiveShipId;
                pilot.ActiveShip.StationId = Depot;
                setup.RemoveRange(
                    setup
                        .Filter<OwnedItem>(item => item.LocationId == workShipId)
                        .Where(item => ShipHoldRules.IsHold(item.FlagId))
                        .ToArray()
                );
                agent.StartingCents = openingCents;
                setup.Set<PilotWallet>().Find(pilotId)!.Cents = openingCents;
                Charter(setup, catalog, pilotId, "Ratting Patrol", "RATP", fixture.Clock.Now);

                setup.Pilots.Find(Customer)!.ActiveShip.StationId = Depot;
                if (setup.Positions.Find(setup.Pilots.Find(Customer)!.ActiveShipId) is { } customerPosition)
                {
                    setup.Remove(customerPosition);
                }
                (setup.Set<PilotWallet>().Find(Customer) ?? setup.Add(new PilotWallet { PilotId = Customer }).Entity).Cents = openingCents;
                int tradeType = (int)MarketSkill.Trade;
                var trade =
                    setup.Set<PilotSkill>().Find(Customer, tradeType)
                    ?? setup.Add(new PilotSkill { PilotId = Customer, TypeId = tradeType }).Entity;
                trade.Points = TrainedSkill.PointsForLevel(catalog.FittingAttributes(tradeType)[DogmaAttributeId.SkillTimeConstant], 2);

                var profile = CapsuleerPatrolFit.Starter(catalog)!;
                var parts = profile
                    .Equipment.GroupBy(item => item.TypeId)
                    .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
                parts[merlin] = 1;
                parts[charge] *= 5;
                foreach (var (type, quantity) in parts)
                {
                    AddStock(setup, Customer, Depot, type, quantity);
                    CapsuleerCommunityStore.GrantRequirements(setup, catalog, pilotId, type, []);
                }
                Assert.True(parts.Values.Sum() * priceCents < openingCents);
                offers = setup
                    .Filter<OwnedItem>(item => item.OwnerId == Customer)
                    .Where(item => item.LocationId == Depot && parts.ContainsKey(item.TypeId))
                    .Select(item => new MarketSale(item.Id, Depot, item.TypeId, priceCents, item.Quantity))
                    .ToArray();
                setup.Commit();
            }
            host.GetRequiredService<IPersonalMarket>().PlaceSell(Customer, offers, CapsuleerReplacementRules.OrderDays);

            using (var work = live.Economy.Begin())
            {
                var docked = ships.FindPilot(work, CapsuleerCommunityStore.MinerAccountId, pilotId)!;
                Assert.True(CapsuleerIndustry.CanStagePvp(work, catalog, docked.SystemId));
                Assert.True(CapsuleerWorkHulls.CanLeaveShip(work, catalog, docked));
                Assert.False(CapsuleerIndustry.HasPvpShip(work, pilotId, workShipId));
                var pending = supply.TryBuyEarnedPvpShip(work, catalog, docked, fixture.Clock.Now);
                Assert.False(pending.Boarded);
                Assert.False(pending.MarketChanged);
                Assert.Equal(workShipId, work.Pilots.Find(pilotId)!.ActiveShipId);
                Assert.Equal(openingCents, work.Set<PilotWallet>().Find(pilotId)!.Cents);
                Assert.Null(CapsuleerCorporations.Organization(work, pilotId).EarnedPvpShipId);
                Assert.Empty(work.Filter<MarketExecution>(row => row.PilotId == pilotId));
                work.Commit();
            }

            using (var work = live.Economy.Begin())
            {
                var pilot = work.Pilots.Find(pilotId)!;
                var station = SystemObjects.Geometry(catalog, Home).Single(body => body.Id == Depot);
                SqliteWorld.SetPosition(work, catalog, workShipId, station.Position, Home, fixture.Clock.Now);
                var target = work.Add(
                    new WorldNpc
                    {
                        Id = NpcEncounterDefinitions.FirstNpcId + 20_000,
                        SystemId = Home,
                        TypeId = pirate,
                        Role = NpcRole.Pirate,
                        X = station.Position.X,
                        Y = station.Position.Y,
                        Z = station.Position.Z,
                        UpdatedAt = fixture.Clock.Now,
                    }
                ).Entity;
                var kill = NpcDamagePersistence.Apply(
                    work,
                    catalog,
                    target,
                    pilot,
                    workShipId,
                    new Dictionary<DogmaAttributeId, double>
                    {
                        [DogmaAttributeId.StructureHitPoints] = 20,
                        [DogmaAttributeId.EntityKillBounty] = bountyIsk,
                    },
                    new DamageVector(100, 0, 0, 0),
                    fixture.Clock.Now
                );
                Assert.True(kill.Destroyed);
                var bounty = Assert.IsType<NpcBounty>(kill.Reward);
                Assert.Equal(bountyIsk * MarketDefinitions.CentsPerIsk, bounty.AmountCents);
                long balance = openingCents + bounty.AmountCents - bounty.TaxCents;
                Assert.Equal(balance, work.Set<PilotWallet>().Find(pilotId)!.Cents);
                ships.Dock(work, catalog, pilotId, workShipId, Depot);
                var docked = ships.FindPilot(work, CapsuleerCommunityStore.MinerAccountId, pilotId)!;

                var turn = supply.TryBuyEarnedPvpShip(work, catalog, docked, fixture.Clock.Now);
                Assert.True(turn.Boarded);
                Assert.True(turn.MarketChanged);
                paidShipId = work.Pilots.Find(pilotId)!.ActiveShipId;
                Assert.NotEqual(workShipId, paidShipId);
                Assert.Equal(merlin, work.Ships.Find(paidShipId)!.TypeId);
                Assert.Equal(Depot, work.Ships.Find(workShipId)!.StationId);
                Assert.Equal(paidShipId, CapsuleerCorporations.Organization(work, pilotId).EarnedPvpShipId);
                Assert.True(CapsuleerIndustry.HasPvpShip(work, pilotId, paidShipId));
                Assert.False(CapsuleerIndustry.HasPvpShip(work, pilotId, workShipId));

                var guns = work.FittedItems(pilotId, paidShipId).Where(item => item.TypeId == railgun && item.Online).ToArray();
                Assert.Equal(2, guns.Length);
                Assert.All(guns, gun => Assert.Single(work.Filter<OwnedItem>(item => item.LocationId == gun.Id && item.TypeId == charge)));
                Assert.Contains(work.FittedItems(pilotId, paidShipId), item => item.TypeId == scrambler && item.Online);
                var purchases = work.Filter<MarketExecution>(row => row.PilotId == pilotId).ToArray();
                Assert.Equal(offers.Length, purchases.Length);
                Assert.All(purchases, row => Assert.True(row.IsBuy && row.CounterpartyId == Customer));
                Assert.Equal(1, purchases.Single(row => row.TypeId == merlin).Quantity);
                Assert.Equal(2, purchases.Single(row => row.TypeId == railgun).Quantity);
                walletAfterPurchase = balance - purchases.Sum(row => row.Quantity * row.PriceCents);
                Assert.Equal(walletAfterPurchase, work.Set<PilotWallet>().Find(pilotId)!.Cents);
                Assert.True(walletAfterPurchase >= openingCents);
                purchaseIds = purchases.Select(row => row.Id).Order().ToArray();
                work.Commit();
            }
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        using (var review = restored.Economy.Begin())
        {
            var current = reopened.GetRequiredService<SqliteWorld>().FindPilot(review, CapsuleerCommunityStore.MinerAccountId, pilotId)!;
            Assert.Equal(paidShipId, current.ShipId);
            Assert.Equal(paidShipId, CapsuleerCorporations.Organization(review, pilotId).EarnedPvpShipId);
            var supply = reopened.GetRequiredService<CapsuleerIndustry>();
            var repeated = supply.TryBuyEarnedPvpShip(review, catalog, current, fixture.Clock.Now);
            Assert.False(repeated.Boarded);
            Assert.False(repeated.MarketChanged);
            Assert.Equal(walletAfterPurchase, review.Set<PilotWallet>().Find(pilotId)!.Cents);
            var restoredPurchases = review.Filter<MarketExecution>(row => row.PilotId == pilotId).Select(row => row.Id).Order();
            Assert.Equal(purchaseIds, restoredPurchases);
            review.Commit();
        }

        using var human = reopened.GetRequiredService<IHumanPresence>().Select(Customer, Home);
        var scheduler = reopened.GetRequiredService<CapsuleerScheduler>();
        var maintenance = reopened.GetRequiredService<CapsuleerMaintenance>();
        scheduler.PopulationPrepared(Home);
        for (int turn = 0; turn < 120 && restored.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work.Ratting?.Patrol is null; turn++)
        {
            fixture.Clock.Seconds++;
            maintenance.Tick();
        }
        Assert.True(scheduler.Read().Failures == 0, scheduler.Read().LastFailure);
        var launched = Assert.IsType<HaulerObservation>(scheduler.Hauler(pilotId));
        Assert.Equal(PatrolKind.Roam, launched.Work.Ratting?.Patrol?.Kind);
        Assert.Equal(paidShipId, launched.Pilot.ShipId);
        Assert.NotNull(launched.Pilot.Position);

        long capsuleId;
        using (var loss = restored.Economy.Begin())
        {
            capsuleId = DestroyBotShip(loss, catalog, pilotId, fixture.Clock.Now);
            Assert.NotEqual(paidShipId, capsuleId);
            Assert.Equal(capsuleId, loss.Pilots.Find(pilotId)!.ActiveShipId);
            Assert.Equal(Depot, loss.Ships.Find(workShipId)!.StationId);
            loss.Commit();
        }

        bool headedToWorkShip = false;
        for (int turn = 0; turn < 600 && !restored.Economy.ActiveShip(pilotId, workShipId); turn++)
        {
            fixture.Clock.Seconds++;
            maintenance.Tick();
            if (
                restored.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work
                    is { ShipId: var ship, Journey: { Kind: TrafficJourneyKind.CapsuleEscape, DestinationStationId: Depot } }
                && ship == capsuleId
            )
            {
                headedToWorkShip = true;
            }
        }
        Assert.True(scheduler.Read().Failures == 0, scheduler.Read().LastFailure);
        Assert.True(headedToWorkShip);
        Assert.True(restored.Economy.ActiveShip(pilotId, workShipId));
        restored.Economy.Read(world =>
        {
            var destroyed = Assert.Single(world.Filter<ShipLoss>(row => row.ShipId == paidShipId));
            Assert.Equal(capsuleId, destroyed.CapsuleId);
            Assert.Null(world.Ships.Find(paidShipId));
            var organization = CapsuleerCorporations.Organization(world, pilotId);
            Assert.Null(organization.EarnedPvpShipId);
            Assert.Null(organization.PvpWorkShipId);
            Assert.Empty(world.Filter<SavedPersonalOrder>(row => row.PilotId == pilotId && row.Status == PersonalOrderStatus.Open));
            return 0;
        });
    }
}
