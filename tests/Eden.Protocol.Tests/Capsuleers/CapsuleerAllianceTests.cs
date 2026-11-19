using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Data.World.Structures;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Protocol.Tests.Sovereignty;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Alliances;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Corporations;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerAllianceTests
{
    private const int HeadquartersSystem = 30000141;
    private const int ObserverSystem = 30000142;
    private const int WingSystem = 31000001;
    private const int ThirdSystem = 31000002;
    private const int FourthSystem = 31000003;
    private const long Observer = 90000001;
    private const long PersonalContribution = 10000;

    [Fact]
    public async Task FundedAllianceStagesOneHubAcrossRestartForAnUnobservedClaimableSystem()
    {
        const int station = 60015157;
        const int hubType = EntosisRules.HubTypeId;
        const int target = SovereigntyCaptureFixture.SystemId;
        const long neutralHub = StructureRules.FirstStructureId + 1;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        var configuration = new WorldConfigurationStore(fixture.Database, fixture.Clock);
        var startup = configuration.Read();
        var draft = startup.Active.Draft;
        configuration.Save(
            startup.Generation,
            draft with
            {
                Settings = draft.Settings with
                {
                    Economy = draft.Settings.Economy with
                    {
                        ProgressiveMarkets = false,
                        StandardMarket = false,
                        ChargeServiceCosts = false,
                    },
                    Capsuleers = draft.Settings.Capsuleers with { TrafficMultiplier = 6 },
                },
            },
            "First claim fixture"
        );
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using (CapsuleerProvisioner provisioner = new(fixture.Database, fixture.Clock))
        {
            provisioner.EnsureSystem(WingSystem);
        }
        PassiveCatalogFixture.LoadSovereignty(fixture.Database.CatalogPath);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (3732,'Empire Control',257);
                INSERT OR REPLACE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (3732,275,5);
                DELETE FROM Stargate WHERE SolarSystemId={target};
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                    (55000001,{HeadquartersSystem},16,10,3500,100000000,0,0),
                    (55000002,{target},16,10,3500,200000000,0,0);
                INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                    (55000001,{HeadquartersSystem},55000002),(55000002,{target},55000001);
                """
            );
        }
        int corporation;
        long sellerPackage;
        long bidId;
        long price;
        long budget;
        long reserve = AllianceBillingRules.Amount(CapsuleerAllianceRules.FoundingCorporations);
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var maintenance = host.GetRequiredService<CorporationMaintenance>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            price = CapsuleerIndustry.ReferencePrice(catalog, hubType);
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                long ceo;
                (corporation, ceo) = Charter(setup, catalog, HeadquartersSystem, "First Claim Works", "FCWK", fixture.Clock.Now);
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                setup.Set<PilotWallet>().Find(ceo)!.Cents = AllianceRules.CreationFeeCents;
                setup.Add(
                    new PilotSkill
                    {
                        PilotId = ceo,
                        TypeId = AllianceRules.EmpireControlSkill,
                        Points = TrainedSkill.PointsForLevel(5, AllianceRules.RequiredEmpireControlLevel),
                    }
                );
                var alliance = host.GetRequiredService<AllianceStore>()
                    .Create(setup, catalog, ceo, new("Claim Coalition", "CLMC", "", ""), fixture.Clock.Now)
                    .Alliance;
                var partner = Charter(setup, catalog, WingSystem, "Neighbor Works", "NWBK", fixture.Clock.Now);
                setup.Add(
                    new SavedAllianceMember
                    {
                        CorporationId = partner.Id,
                        AllianceId = alliance.Id,
                        JoinedAt = fixture.Clock.Now,
                    }
                );
                setup.Pilots.Find(Observer)!.ActiveShip.StationId = station;
                sellerPackage = setup.NextItemId();
                setup.Add(
                    new OwnedItem
                    {
                        Id = sellerPackage,
                        OwnerId = Observer,
                        TypeId = hubType,
                        Quantity = 1,
                        LocationId = station,
                        FlagId = (int)InventoryFlag.Hangar,
                    }
                );
                budget = price + PilotMarketStore.SupplyBrokerFee(setup, catalog, ceo, station, price, 1) + reserve;
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = budget;
                setup.Add(
                    new SavedStructure
                    {
                        Id = neutralHub,
                        TypeId = hubType,
                        Name = "Neutral Sovereignty Hub",
                        CorporationId = EntosisRules.NeutralCorporationId,
                        SystemId = target,
                        State = StructureState.ShieldVulnerable,
                    }
                );
                setup.Commit();
            }
            maintenance.Tick();
            Assert.Empty(live.Economy.Read(world => world.Set<SavedPersonalOrder>().ToArray()));

            using var occupied = host.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem);
            maintenance.Tick();
            Assert.Empty(live.Economy.Read(world => world.Set<SavedPersonalOrder>().ToArray()));
            using (var unfund = live.Economy.Begin(TransactionParticipants.None))
            {
                Assert.Equal(budget, unfund.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                unfund.Remove(unfund.Set<SavedStructure>().Find(neutralHub)!);
                unfund.Set<SavedCorporation>().Find(corporation)!.BalanceCents = price + reserve - 1;
                unfund.Commit();
            }
            fixture.Clock.Seconds += 16;
            maintenance.Tick();
            Assert.Empty(live.Economy.Read(world => world.Set<SavedPersonalOrder>().ToArray()));
            using (var fund = live.Economy.Begin(TransactionParticipants.None))
            {
                Assert.Equal(price + reserve - 1, fund.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                fund.Set<SavedCorporation>().Find(corporation)!.BalanceCents = budget;
                fund.Commit();
            }

            fixture.Clock.Seconds += 16;
            maintenance.Tick();
            var order = Assert.Single(live.Economy.Read(world => world.Set<SavedPersonalOrder>().ToArray()));
            Assert.Equal(
                (corporation, station, hubType, true, 1, price),
                (order.CorporationId, order.StationId, order.TypeId, order.Bid, order.Remaining, order.EscrowCents)
            );
            bidId = order.Id;
            Assert.Equal(reserve, live.Economy.Read(world => world.Set<SavedCorporation>().Find(corporation)!.BalanceCents));
            await live.FlushAsync();
        }

        using (var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            using var observing = reopened.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem);
            LiveWorld restored = reopened.GetRequiredService<LiveWorld>();
            using (var deposit = restored.Economy.Begin(TransactionParticipants.None))
            {
                Assert.Equal(reserve, deposit.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                // Fund a second hub to prove the open order prevents another purchase.
                deposit.Set<SavedCorporation>().Find(corporation)!.BalanceCents = budget;
                deposit.Commit();
            }
            reopened.GetRequiredService<CorporationMaintenance>().Tick();
            Assert.Equal(bidId, Assert.Single(restored.Economy.Read(world => world.Set<SavedPersonalOrder>().ToArray())).Id);
            reopened.GetRequiredService<IPilotMarket>().Sell(Observer, [new(sellerPackage, station, hubType, price, 1)]);
            await restored.FlushAsync();
        }

        using var delivered = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var present = delivered.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem);
        delivered.GetRequiredService<CorporationMaintenance>().Tick();
        delivered
            .GetRequiredService<LiveWorld>()
            .Economy.Read(world =>
            {
                var bid = Assert.Single(world.Filter<SavedPersonalOrder>(order => order.CorporationId == corporation));
                Assert.Equal(bidId, bid.Id);
                Assert.Equal(0, bid.Remaining);
                Assert.Equal(0, bid.EscrowCents);
                var hub = Assert.Single(world.Filter<OwnedItem>(item => item.OwnerId == bid.PilotId && item.TypeId == hubType));
                Assert.Equal(
                    (hubType, (long)station, (int)InventoryFlag.Hangar, 1),
                    (hub.TypeId, hub.LocationId, hub.FlagId, hub.Quantity)
                );
                Assert.Equal(budget, world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                return true;
            });
    }

    [Fact]
    public async Task FundedNeighborsFormAndGrowAnAllianceThroughDueApplicationsAcrossRestart()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (3732,'Empire Control',257);
                INSERT OR REPLACE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (3732,275,5);
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                    (55000001,31000002,16,10,3500,0,0,0),(55000002,31000005,16,10,3500,0,0,0),
                    (55000003,31000003,16,10,3500,0,0,0),(55000004,31000005,16,10,3500,0,0,0);
                INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                    (55000001,31000002,55000002),(55000002,31000005,55000001),
                    (55000003,31000003,55000004),(55000004,31000005,55000003);
                """
            );
        }
        var configuration = new WorldConfigurationStore(fixture.Database, fixture.Clock);
        var startup = configuration.Read();
        var draft = startup.Active.Draft;
        configuration.Save(
            startup.Generation,
            draft with
            {
                Settings = draft.Settings with { Capsuleers = draft.Settings.Capsuleers with { TrafficMultiplier = 6 } },
            },
            "Alliance fixture"
        );
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using (CapsuleerProvisioner provisioner = new(fixture.Database, fixture.Clock))
        {
            foreach (int system in new[] { WingSystem, ThirdSystem, FourthSystem })
            {
                provisioner.EnsureSystem(system);
            }
        }

        (int Id, long Ceo) founder;
        (int Id, long Ceo) wing;
        (int Id, long Ceo) third;
        (int Id, long Ceo) fourth;
        int allianceId;
        long admission;
        long billDue;
        long reserve = AllianceBillingRules.Amount(CapsuleerAllianceRules.FoundingCorporations);
        long shortfall = AllianceRules.CreationFeeCents - PersonalContribution;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var maintenance = host.GetRequiredService<CorporationMaintenance>();
            var alliances = host.GetRequiredService<IAlliances>();
            var applications = host.GetRequiredService<IAllianceMembership>();
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
                founder = Charter(setup, catalog, HeadquartersSystem, "Manufacturing Fleet", "MFCT", fixture.Clock.Now);
                wing = Charter(setup, catalog, WingSystem, "Regional Patrol", "RPAT", fixture.Clock.Now);
                third = Charter(setup, catalog, ThirdSystem, "Supply Fleet", "SFLT", fixture.Clock.Now);
                fourth = Charter(setup, catalog, FourthSystem, "Route Logistics", "RLGS", fixture.Clock.Now);
                setup.Set<PilotWallet>().Find(founder.Ceo)!.Cents = PersonalContribution;
                setup.Set<SavedCorporation>().Find(founder.Id)!.BalanceCents = shortfall + reserve - 1;
                setup.Commit();
            }

            using (host.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem))
            {
                maintenance.Tick();
                Assert.Empty(alliances.List());
                live.Economy.Read(world =>
                {
                    Assert.Null(world.Set<PilotSkill>().Find(founder.Ceo, AllianceRules.EmpireControlSkill));
                    Assert.Empty(world.Set<SavedServicePayment>());
                    Assert.Equal(PersonalContribution, world.Set<PilotWallet>().Find(founder.Ceo)!.Cents);
                    return true;
                });
            }
            using (var funding = live.Economy.Begin(TransactionParticipants.None))
            {
                funding.Set<SavedCorporation>().Find(founder.Id)!.BalanceCents++;
                funding.Commit();
            }
            fixture.Clock.Seconds += 60;
            maintenance.Tick();
            Assert.Empty(alliances.List());

            using var present = host.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem);
            maintenance.Tick();
            var alliance = Assert.Single(alliances.List());
            allianceId = alliance.Id;
            Assert.Equal(founder.Id, alliance.ExecutorCorporationId);
            Assert.Equal(founder.Ceo, alliance.CreatorId);
            Assert.Equal(founder.Id, Assert.Single(alliances.Members(allianceId)).CorporationId);
            Assert.False(CapsuleerCorporations.CanFlyTogether(live, founder.Ceo, wing.Ceo));
            var accepted = Assert.Single(applications.Applications(wing.Ceo, forAlliance: false));
            Assert.Equal(AllianceApplicationStatus.Accepted, accepted.Status);
            admission = Assert.IsType<long>(accepted.EffectiveAt);
            Assert.Equal(fixture.Clock.Now + AllianceMembershipRules.AdmissionDelay.Ticks, admission);
            billDue = Assert.Single(host.GetRequiredService<IAllianceBilling>().Bills(founder.Ceo)).DueAt;
            live.Economy.Read(world =>
            {
                Assert.Equal(0, world.Set<PilotWallet>().Find(founder.Ceo)!.Cents);
                Assert.Equal(reserve, world.Set<SavedCorporation>().Find(founder.Id)!.BalanceCents);
                Assert.Equal(-shortfall, Assert.Single(world.Filter<SavedWalletLedger>(row => row.OwnerId == founder.Id)).AmountCents);
                var charge = Assert.Single(world.Set<SavedServicePayment>());
                Assert.Equal((WalletEntryType.AllianceRegistration, -AllianceRules.CreationFeeCents), (charge.Kind, charge.AmountCents));
                long points = world.Set<PilotSkill>().Find(founder.Ceo, AllianceRules.EmpireControlSkill)!.Points;
                Assert.Equal(TrainedSkill.PointsForLevel(5, AllianceRules.RequiredEmpireControlLevel), points);
                return true;
            });
            maintenance.Tick();
            Assert.Equal(accepted, Assert.Single(applications.Applications(wing.Ceo, forAlliance: false)));
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        var worker = reopened.GetRequiredService<CorporationMaintenance>();
        var roster = reopened.GetRequiredService<IAlliances>();
        var membership = reopened.GetRequiredService<IAllianceMembership>();
        using var occupied = reopened.GetRequiredService<IHumanPresence>().Select(Observer, ObserverSystem);
        fixture.Clock.UtcNow = DateTimeOffset.FromFileTime(admission - 1);
        worker.Tick();
        Assert.Null(roster.Affiliation(wing.Id));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        worker.Tick();
        Assert.Equal(allianceId, roster.PilotAffiliation(wing.Ceo));
        Assert.True(CapsuleerCorporations.CanFlyTogether(restored, founder.Ceo, wing.Ceo));
        Assert.Equal(admission, roster.Members(allianceId).Single(member => member.CorporationId == wing.Id).JoinedAt);

        var manual = membership.Apply(third.Ceo, allianceId, "Awaiting the directors' vote.");
        using (var funding = restored.Economy.Begin(TransactionParticipants.None))
        {
            funding.Set<SavedCorporation>().Find(founder.Id)!.BalanceCents = AllianceBillingRules.Amount(
                CapsuleerAllianceRules.TargetCorporations
            );
            funding.Add(new SavedCapsuleerSuspension { PilotId = fourth.Ceo });
            funding.Commit();
        }
        fixture.Clock.Seconds += 60;
        worker.Tick();
        Assert.Equal(manual, Assert.Single(membership.Applications(third.Ceo, forAlliance: false)));
        membership.Withdraw(third.Ceo, allianceId);
        fixture.Clock.Seconds += 60;
        worker.Tick();
        var thirdApplication = Assert.Single(membership.Applications(third.Ceo, forAlliance: false));
        Assert.Equal(AllianceApplicationStatus.Accepted, thirdApplication.Status);
        using (var enable = restored.Economy.Begin(TransactionParticipants.None))
        {
            enable.Remove(enable.Set<SavedCapsuleerSuspension>().Find(fourth.Ceo)!);
            enable.Commit();
        }
        fixture.Clock.Seconds += 60;
        worker.Tick();
        Assert.Empty(membership.Applications(fourth.Ceo, forAlliance: false));
        fixture.Clock.UtcNow = DateTimeOffset.FromFileTime(thirdApplication.EffectiveAt!.Value);
        worker.Tick();
        Assert.Equal(new[] { founder.Id, wing.Id, third.Id }, roster.Members(allianceId).Select(member => member.CorporationId));

        fixture.Clock.UtcNow = DateTimeOffset.FromFileTime(billDue);
        worker.Tick();
        worker.Tick();
        var bills = reopened.GetRequiredService<IAllianceBilling>().Bills(founder.Ceo);
        Assert.Equal(founder.Id, bills[0].PaidByCorporationId);
        Assert.Equal(AllianceBillingRules.Amount(CapsuleerAllianceRules.TargetCorporations), bills[1].AmountCents);
        restored.Economy.Read(world =>
        {
            Assert.Single(world.Set<SavedServicePayment>(), charge => charge.Kind == WalletEntryType.AllianceRegistration);
            Assert.Single(
                world.Filter<SavedWalletLedger>(row => row.OwnerId == founder.Id),
                row => row.EntryType == WalletEntryType.AllianceMaintenance
            );
            return true;
        });
        await restored.FlushAsync();
    }

    private static (int Id, long Ceo) Charter(
        EconomyTransaction world,
        CatalogDbContext catalog,
        int system,
        string name,
        string ticker,
        long now
    )
    {
        var pilots = world
            .Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == system)
            .OrderBy(agent => agent.PilotId)
            .Take(CapsuleerCorporationRules.FoundingRoster)
            .Select(agent => agent.PilotId)
            .ToArray();
        Assert.Equal(CapsuleerCorporationRules.FoundingRoster, pilots.Length);
        long ceo = pilots[0];
        var founder = world.Pilots.Find(ceo)!;
        if (world.Positions.Find(founder.ActiveShipId) is { } position)
        {
            world.Remove(position);
        }
        founder.ActiveShip.StationId = catalog.Definitions.StationsInSystem(system)[0].Id;
        world.Add(
            new PilotSkill
            {
                PilotId = ceo,
                TypeId = CorporationRules.ManagementSkill,
                Points = 250,
            }
        );
        world.Set<PilotWallet>().Find(ceo)!.Cents += CorporationRules.CreationFeeCents;
        int id = PlayerCorporationStore.Create(world, catalog, ceo, PlayerCorporationTests.Details(name, ticker), now).Corporation.Id;
        world.Set<SavedCorporation>().Find(id)!.CapsuleerFoundingSystemId = system;
        foreach (long pilot in pilots.Skip(1))
        {
            CorporationMembershipStore.Move(world, world.Pilots.Find(pilot)!, id, now);
        }
        return (id, ceo);
    }
}
