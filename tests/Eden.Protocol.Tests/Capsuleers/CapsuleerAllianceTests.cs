using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Corporations;
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
