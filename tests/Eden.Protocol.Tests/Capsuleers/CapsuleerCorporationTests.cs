using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Docking;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.Corporations;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.Server.Chat;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerCorporationTests
{
    private const int Home = 30000141;
    private const long Human = 90000001;

    [Fact]
    public async Task ExistingSchoolPilotsFoundAndJoinOnceAcrossCheckpointAndRestart()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PrepareExistingSave(fixture);
        int corporation;
        long ceo;
        string ticker;
        long[] members;
        Dictionary<long, long> ships;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var corps = host.GetRequiredService<IPlayerCorporations>();
            using var presence = host.GetRequiredService<IHumanPresence>().Select(Human, Home);
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(Home);
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            var population = host.GetRequiredService<ICapsuleerPopulation>().Agents(Home);
            ships = population.ToDictionary(agent => agent.PilotId, agent => agent.FounderShipId);
            var originalWallets = live.Economy.Read(world =>
                population.ToDictionary(agent => agent.PilotId, agent => world.Set<PilotWallet>().Find(agent.PilotId)!.Cents)
            );
            var originalJobs = scheduler.Jobs().ToArray();
            var chat = host.GetRequiredService<ChatRuntime>();
            Guid session = Guid.NewGuid();
            chat.Update(session, new(Human, "Local observer", 1000044, Home, 0));
            var connection = chat.Authenticate(Human.ToString(System.Globalization.CultureInfo.InvariantCulture), chat.IssueToken(session));
            ChatRoom local = new(ChatRoomKind.Local, Home);
            chat.Join(connection.Id, local, null);
            var observer = host.GetRequiredService<SqliteWorld>().FindPilot(1000001, Human)!;
            var guests = host.GetRequiredService<SharedStationGuests>();
            maintenance.Refresh();
            Assert.Contains(
                guests.List(observer),
                guest => ships.ContainsKey(guest.PilotId) && guest.CorpId < CorporationRules.FirstCorporationId
            );
            Assert.Contains(
                chat.Members(connection.Id, local),
                pilot => ships.ContainsKey(pilot.Id) && pilot.CorporationId < CorporationRules.FirstCorporationId
            );
            var preparation = host.GetRequiredService<ICapsuleerBackgroundPreparation>();
            CapsuleerBackgroundTurn turn = preparation.PrepareNext();
            for (int batch = 0; !turn.Complete && batch < 32; batch++)
            {
                Assert.Null(turn.Failure);
                turn = preparation.PrepareNext();
            }
            Assert.Null(turn.Failure);
            Assert.True(turn.Complete);
            maintenance.Refresh();
            Assert.Contains(guests.List(observer), guest => guest.CorpId >= CorporationRules.FirstCorporationId);
            Assert.Equal(originalJobs, scheduler.Jobs());
            Assert.True(
                live.Economy.Read(world => originalWallets.All(entry => world.Set<PilotWallet>().Find(entry.Key)!.Cents == entry.Value))
            );
            var row = Assert.Single(live.Economy.Read(world => world.Set<SavedCorporation>().ToArray()));
            corporation = row.Id;
            ceo = row.CeoId;
            ticker = row.Ticker;
            members = corps.MemberNames(ceo).Select(member => member.PilotId).ToArray();
            Assert.True(members.Length >= 3, $"Only {members.Length} pilots joined.");
            Assert.Contains(guests.List(observer), guest => guest.CorpId == corporation && members.Contains(guest.PilotId));
            Assert.Contains(chat.Members(connection.Id, local), pilot => pilot.CorporationId == corporation && members.Contains(pilot.Id));
            Assert.Equal(Home, row.CapsuleerFoundingSystemId);
            Assert.Equal(0, row.BalanceCents);
            Assert.Equal(CapsuleerCorporationRules.BountyTaxRate, row.Snapshot(members.Length).Details.TaxRate);
            var result = host.GetRequiredService<CorporationPublicInfoHandler>()
                .Call(null!, "corpmgr", "GetPublicInfo", [(long)corporation]);
            var info = Assert.IsType<CorporationPublicInfoReply>(Assert.IsType<PilotCallResult.Success>(result).Reply);
            Assert.Equal((ceo, ticker, members.Length), (info.CeoId, info.Ticker, info.MemberCount));
            var directory = Assert.Single(host.GetRequiredService<IPlayerCorporationDirectory>().Read([corporation]));
            Assert.NotNull(directory.Logo.Shape1);
            foreach (long member in members)
            {
                Assert.Equal(corporation, corps.Identity(member).Id);
                Assert.Equal(corporation, corps.Employment(member)[0].CorporationId);
                Assert.True(corps.Employment(member)[1].CorporationId < CorporationRules.FirstCorporationId);
                var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(CapsuleerCommunityStore.MinerAccountId, member)!;
                Assert.Equal(ships[member], pilot.ShipId);
                Assert.Equal(ticker, pilot.CorporationTicker);
            }
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IPlayerCorporations>();
        Assert.Equal(members, restored.MemberNames(ceo).Select(member => member.PilotId));
        Assert.Equal(ticker, restored.Read(corporation)!.Details.Ticker);
        Assert.Equal(CapsuleerCorporationRules.BountyTaxRate, restored.Read(corporation)!.Details.TaxRate);
        var economy = reopened.GetRequiredService<LiveWorld>().Economy;
        using var retry = economy.Begin(TransactionParticipants.None);
        using CatalogDbContext source = new(fixture.Database.CatalogPath);
        Assert.All(
            members,
            member => Assert.False(CapsuleerCorporations.Place(retry, source, fixture.Database, member, fixture.Clock.Now))
        );
        Assert.False(CapsuleerCorporations.Place(retry, source, fixture.Database, Human, fixture.Clock.Now));
        Assert.Equal(ceo, Assert.Single(retry.Set<SavedCorporationPayment>()).PilotId);
        Assert.Equal(CorporationRules.CreationFeeCents, Assert.Single(retry.Set<SavedWalletLedger>()).AmountCents);
        Assert.True(retry.Set<PilotSkill>().Find(ceo, CorporationRules.ManagementSkill)!.Points >= 250);
        Assert.All(members, member => Assert.Equal(2, retry.Filter<SavedCorporationEmployment>(row => row.PilotId == member).Count()));
    }

    [Fact]
    public void IneligiblePilotsAndRolledBackFoundingLeaveAssetsAndAllowanceIntact()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PrepareExistingSave(fixture);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var economy = host.GetRequiredService<LiveWorld>().Economy;
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        var roster = host.GetRequiredService<ICapsuleerPopulation>().Agents(Home).OrderBy(agent => agent.PilotId).ToArray();
        long founder = roster[0].PilotId;
        bool Place(EconomyTransaction transaction, long pilot) =>
            CapsuleerCorporations.Place(transaction, catalog, fixture.Database, pilot, fixture.Clock.Now);
        using (var sparse = economy.Begin(TransactionParticipants.None))
        {
            foreach (var agent in roster.Skip(CapsuleerCorporationRules.FoundingRoster - 1))
            {
                sparse.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
            }
            Assert.False(Place(sparse, founder));
            Assert.False(Place(sparse, roster[^1].PilotId));
            Assert.Empty(sparse.Set<SavedCorporation>());
        }
        using (var flying = economy.Begin(TransactionParticipants.None))
        {
            long ship = flying.Pilots.Find(founder)!.ActiveShipId;
            flying.Add(new FlightPosition { ShipId = ship, SystemId = Home });
            Assert.False(Place(flying, founder));
            Assert.Empty(flying.Set<SavedCorporationPayment>());
        }
        long originalBalance;
        using (var staged = economy.Begin(TransactionParticipants.None))
        {
            originalBalance = staged.Set<PilotWallet>().Find(founder)!.Cents;
            Assert.True(Place(staged, founder));
            Assert.Equal(originalBalance, staged.Set<PilotWallet>().Find(founder)!.Cents);
            Assert.Single(staged.Set<SavedCorporationPayment>());
        }
        using var retry = economy.Begin(TransactionParticipants.None);
        Assert.Equal(originalBalance, retry.Set<PilotWallet>().Find(founder)!.Cents);
        Assert.Empty(retry.Set<SavedCorporationPayment>());
        Assert.Empty(retry.Set<SavedCorporationEmployment>());
        Assert.Empty(retry.Set<SavedWalletLedger>());
        Assert.True(Place(retry, founder));
        // The archived charter consumes the initial allowance even if its members leave.
        retry.Set<SavedCorporation>().Single().ClosedAt = fixture.Clock.Now;
        Assert.False(Place(retry, roster[1].PilotId));
        Assert.Single(retry.Set<SavedWalletLedger>());
    }

    [Fact]
    public void NearbyMemberJoinsOnceAndLaterFounderPaysForASecondCharter()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PrepareExistingSave(fixture);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock);
        const int neighbor = 31000001;
        provisioner.EnsureSystem(neighbor);
        provisioner.EnsureSystem(neighbor + 1);
        provisioner.EnsureSystem(neighbor + 2);
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        using var world = host.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None);
        var local = world.Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == Home).OrderBy(agent => agent.PilotId).ToArray();
        var visitor = world.Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == neighbor).First();
        long ship = world.Pilots.Find(visitor.PilotId)!.ActiveShipId;
        if (world.Positions.Find(ship) is { } position)
        {
            world.Remove(position);
        }
        bool Place(long pilot) => CapsuleerCorporations.Place(world, catalog, fixture.Database, pilot, fixture.Clock.Now);
        Assert.True(Place(local[0].PilotId));
        var first = Assert.Single(world.Set<SavedCorporation>());
        first.MemberLimit = 1;
        Assert.False(Place(local[1].PilotId));
        first.MemberLimit = CorporationRules.MembersPerManagementLevel;
        first.CeoId = Human;
        Assert.False(Place(local[1].PilotId));
        first.CeoId = local[0].PilotId;
        Assert.True(Place(visitor.PilotId));
        Assert.Equal(first.Id, world.Pilots.Find(visitor.PilotId)!.CorporationId);
        Assert.False(Place(visitor.PilotId));
        first.ClosedAt = fixture.Clock.Now;
        long nextFounder = local[1].PilotId;
        var wallet = world.Set<PilotWallet>().Find(nextFounder)!;
        wallet.Cents = CorporationRules.CreationFeeCents;
        Assert.False(Place(nextFounder));
        world.Add(
            new PilotSkill
            {
                PilotId = nextFounder,
                TypeId = CorporationRules.ManagementSkill,
                Points = 250,
            }
        );
        Assert.True(Place(nextFounder));
        var second = Assert.Single(world.Set<SavedCorporation>(), row => row.Id != first.Id);
        Assert.NotEqual(first.Ticker, second.Ticker);
        Assert.Equal(0, wallet.Cents);
        Assert.Equal(2, world.Set<SavedCorporationPayment>().Count());
        Assert.Single(world.Set<SavedWalletLedger>());
    }

    [Fact]
    public async Task NativeApplicationAndDeferredInvitationUseOrdinaryMembershipHistory()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PrepareExistingSave(fixture);
        const long recruiter = 90000002;
        int hiringCorporation;
        int botCorporation;
        long recruit;
        long recruitShip;
        long invitation;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var membership = host.GetRequiredService<ICorporationMembership>();
            var corporations = host.GetRequiredService<IPlayerCorporations>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var world = live.Economy.Begin(TransactionParticipants.None))
            {
                var agents = world.Filter<SavedCapsuleerAgent>(a => a.CommunitySystemId == Home).OrderBy(a => a.PilotId).ToArray();
                Assert.True(CapsuleerCorporations.Place(world, catalog, fixture.Database, agents[0].PilotId, fixture.Clock.Now));
                recruit = agents[1].PilotId;
                Assert.True(CapsuleerCorporations.Place(world, catalog, fixture.Database, recruit, fixture.Clock.Now));
                botCorporation = world.Pilots.Find(recruit)!.CorporationId;
                recruitShip = world.Pilots.Find(recruit)!.ActiveShipId;
                world.Add(
                    new FlightPosition
                    {
                        ShipId = recruitShip,
                        SystemId = Home,
                        X = 1000,
                    }
                );
                world.Add(
                    new PilotSkill
                    {
                        PilotId = recruiter,
                        TypeId = CorporationRules.ManagementSkill,
                        Points = 250,
                    }
                );
                world.Add(new PilotWallet { PilotId = recruiter, Cents = CorporationRules.CreationFeeCents });
                world.Ships.Find(world.Pilots.Find(recruiter)!.ActiveShipId)!.StationId = 60015157;
                world.Commit();
            }
            using var applicant = host.CreateScope();
            var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001, Human)!;
            using PilotCallContext context = new(
                1000001,
                pilot.Account,
                [pilot],
                applicant.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            );
            context.Select(pilot);
            applicant.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Enter(context);
            var router = applicant.ServiceProvider.GetRequiredService<PilotCallRouter>();
            PilotCallResult Call(string method, object?[] args) =>
                router.Call(context, "corpRegistry", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { });
            Assert.IsType<PilotCallResult.Success>(Call("InsertApplication", [(long)botCorporation, "I'd like to join."]));
            var offer = Assert.Single(membership.Applications(Human, false));
            Assert.Equal(CorporationApplicationStatus.Offered, offer.Status);
            Assert.Contains(context.Notifications, message => message.Name == "OnCorporationApplicationChanged");
            Assert.NotEqual(botCorporation, corporations.Identity(Human).Id);
            Assert.IsType<PilotCallResult.Success>(
                Call("UpdateApplicationOffer", CorporationMembershipTests.UpdateArgs(offer, CorporationApplicationStatus.Joined))
            );
            Assert.Equal(botCorporation, corporations.Identity(Human).Id);

            hiringCorporation = corporations
                .Create(recruiter, PlayerCorporationTests.Details() with { ApplicationsEnabled = false })
                .Corporation.Id;
            var invited = membership.Invite(recruiter, recruit);
            invitation = invited.Id;
            Assert.Equal(CorporationApplicationStatus.Invited, invited.Status);
            Assert.Equal(botCorporation, corporations.Identity(recruit).Id);
            await live.FlushAsync();
        }

        using (WorldDbContext saved = new(fixture.Database.Path))
        {
            saved.Database.ExecuteSql(
                $"""
                UPDATE WorldCorporation SET DetailsJson=json_set(DetailsJson,'$.ApplicationsEnabled',json('false'))
                WHERE Id={botCorporation};
                """
            );
            saved.Database.ExecuteSqlRaw("PRAGMA user_version=187;");
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var persisted = reopened.GetRequiredService<ICorporationMembership>();
        Assert.Equal(invitation, Assert.Single(persisted.Applications(recruit, false)).Id);
        var restored = reopened.GetRequiredService<LiveWorld>();
        using (var dock = restored.Economy.Begin(TransactionParticipants.None))
        {
            dock.Remove(dock.Positions.Find(recruitShip)!);
            const int deliveryStation = 60015157;
            HaulerProgress stopped = new(HaulerActivity.Docked, 0, 0, recruitShip, deliveryStation, 0, new NavigationState().Capture())
            {
                Freight = new(1, deliveryStation, [new(1, 34, 1)]),
            };
            new HaulerCheckpoint(recruit, stopped).Apply(dock);
            var ship = dock.Ships.Find(recruitShip)!;
            ship.StationId = 60003760;
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            Assert.False(CapsuleerCorporations.Place(dock, catalog, fixture.Database, recruit, fixture.Clock.Now));
            Assert.Equal(CorporationApplicationStatus.Invited, dock.Set<SavedCorporationApplication>().Find(invitation)!.Status);
            ship.StationId = deliveryStation;
            dock.Commit();
        }
        using var human = reopened.GetRequiredService<IHumanPresence>().Select(Human, Home);
        var preparation = reopened.GetRequiredService<ICapsuleerBackgroundPreparation>();
        for (int batch = 0; batch < 32; batch++)
        {
            var turn = preparation.PrepareNext();
            Assert.Null(turn.Failure);
            if (turn.Complete)
            {
                break;
            }
        }
        var after = reopened.GetRequiredService<IPlayerCorporations>();
        Assert.True(after.Read(botCorporation)!.Details.ApplicationsEnabled);
        Assert.False(after.Read(hiringCorporation)!.Details.ApplicationsEnabled);
        Assert.Equal(CorporationApplicationStatus.Joined, Assert.Single(persisted.Applications(recruit, false)).Status);
        Assert.Equal(hiringCorporation, after.Identity(recruit).Id);
        Assert.Equal(botCorporation, after.Employment(recruit)[1].CorporationId);
        Assert.Equal(recruitShip, reopened.GetRequiredService<SqliteWorld>().FindPilot(-1, recruit)!.ShipId);
        Assert.Contains(persisted.Changes(recruiter, 0), change => change.Membership?.PilotId == recruit);
        await restored.FlushAsync();
    }

    internal static void PrepareExistingSave(CapsuleerHaulerTests.Fixture fixture)
    {
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (3363,'Corporation Management',257);
                INSERT OR IGNORE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (3363,275,1);
                """
            );
        }
        for (int i = 1; i <= 5; i++)
        {
            CapsuleerProvisioningTests.AddSystem(fixture.Database, 31000000 + i);
        }
        using (var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock))
        {
            provisioner.EnsureSystem(Home);
        }
        using (WorldDbContext saved = new(fixture.Database.Path))
        {
            Assert.True(saved.Set<SavedCapsuleerAgent>().Count() >= CapsuleerCorporationRules.FoundingRoster);
            foreach (var agent in saved.Set<SavedCapsuleerAgent>().ToArray())
            {
                var ship = saved.Ships.Find(agent.FounderShipId)!;
                if (saved.Positions.Find(ship.Id) is { } position)
                {
                    saved.Remove(position);
                }
                ship.StationId = 60015157;
                if (saved.Set<SavedCapsuleerHauler>().SingleOrDefault(row => row.PilotId == agent.PilotId) is { } hauler)
                {
                    hauler.Progress = null;
                }
            }
            saved.SaveChanges();
            saved.Database.ExecuteSqlRaw("ALTER TABLE WorldCorporation DROP COLUMN CapsuleerFoundingSystemId; PRAGMA user_version=186;");
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
    }
}
