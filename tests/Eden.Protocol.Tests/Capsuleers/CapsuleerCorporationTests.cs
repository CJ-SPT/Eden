using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
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
using Eden.World.Configuration;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Inventory;
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
    public void SeededCharterIdentitiesHaveVarietyAndResolveCollisions()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> tickers = new(StringComparer.OrdinalIgnoreCase);
        int retries = 0;
        for (int founder = 0; founder < 500; founder++)
        {
            CorporationDetails Draw(int attempt) =>
                CapsuleerCorporationRules.Identity("Jita", 27, Human + founder, "Ria Vey", attempt, founder % 2 == 0, false);
            var first = Draw(0);
            Assert.Equal(first, Draw(0));
            Assert.False(first.Name.StartsWith("Jita", StringComparison.Ordinal));
            Assert.Contains("Jita", first.Description);
            var accepted = Enumerable
                .Range(0, CapsuleerCorporationRules.MaximumIdentityAttempts)
                .First(attempt => !names.Contains(Draw(attempt).Name) && !tickers.Contains(Draw(attempt).Ticker));
            retries += accepted;
            var identity = Draw(accepted);
            Assert.True(names.Add(identity.Name));
            Assert.True(tickers.Add(identity.Ticker));
        }
        Assert.Equal(500, names.Count);
        Assert.InRange(retries, 1, 500);
        Assert.Contains(names, name => !name.Contains(' '));
        Assert.Contains(names, name => name.Contains("Cooperative", StringComparison.Ordinal));
        Assert.Contains(names, name => name.StartsWith("Vey ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyRenameRequiresMatchingPreviewAndRefreshesResidentOwnerName()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PrepareExistingSave(fixture);
        CapsuleerCorporationNamePreview preview;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            int id;
            using (var world = live.Economy.Begin(TransactionParticipants.None))
            {
                long founder = world.Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == Home).First().PilotId;
                Assert.True(CapsuleerCorporations.Place(world, catalog, fixture.Database, founder, fixture.Clock.Now));
                var charter = Assert.Single(world.Set<SavedCorporation>());
                id = charter.Id;
                long seed = world.Set<SavedCapsuleerProvisioning>().Find(1)?.WorldSeed ?? 0;
                var legacy = CapsuleerCorporationRules.LegacyIdentity(catalog.RequireSystem(Home).Name, seed, Home, 0, true, false);
                charter.Name = legacy.Name;
                charter.Ticker = legacy.Ticker;
                charter.DetailsJson = System.Text.Json.JsonSerializer.Serialize(legacy);
                world.Commit();
            }
            var store = new CapsuleerCorporationIdentityStore(fixture.Database);
            preview = Assert.IsType<CapsuleerCorporationNamePreview>(store.Preview(id));
            var directory = host.GetRequiredService<IPlayerCorporationDirectory>();
            Assert.Equal(preview.CurrentName, Assert.Single(directory.Read([id])).Name);
            Assert.False(store.Apply(preview with { CurrentName = "Stale charter" }));
            Assert.True(store.Apply(preview));
            Assert.Null(store.Preview(id));
            Assert.Equal(preview.ProposedName, Assert.Single(directory.Read([id])).Name);
            await live.FlushAsync();
        }
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var identity = reopened.GetRequiredService<IPlayerCorporations>().Read(preview.CorporationId)!;
        Assert.Equal((preview.ProposedName, preview.Ticker), (identity.Details.Name, identity.Details.Ticker));
    }

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
            using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
            using (var placement = live.Economy.Begin(TransactionParticipants.None))
            {
                Assert.True(CapsuleerCorporations.Place(placement, catalog, fixture.Database, population[0].PilotId, fixture.Clock.Now));
                foreach (var agent in population.Skip(1))
                {
                    CompleteRecruitment(placement, catalog, fixture, agent.PilotId);
                }
                placement.Commit();
            }
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
        bool Place(long pilot) =>
            CapsuleerCorporations.Place(
                world,
                catalog,
                fixture.Database,
                pilot,
                fixture.Clock.Now,
                CapsuleerCorporations.Organization(world, pilot).DueTicks
            );
        Assert.True(Place(local[0].PilotId));
        var first = Assert.Single(world.Set<SavedCorporation>());
        first.MemberLimit = 1;
        Assert.False(Place(local[1].PilotId));
        first.MemberLimit = CorporationRules.MembersPerManagementLevel;
        first.CeoId = Human;
        Assert.False(Place(local[1].PilotId));
        first.CeoId = local[0].PilotId;
        Assert.True(CompleteRecruitment(world, catalog, fixture, visitor.PilotId));
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
                Assert.True(CompleteRecruitment(world, catalog, fixture, recruit));
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
        Assert.Equal(
            CorporationApplicationStatus.Invited,
            Assert.Single(persisted.Applications(recruit, false), a => a.Id == invitation).Status
        );
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
            long due = CapsuleerCorporations.Organization(dock, recruit).DueTicks;
            Assert.False(CapsuleerCorporations.Place(dock, catalog, fixture.Database, recruit, fixture.Clock.Now, due));
            Assert.Equal(CorporationApplicationStatus.Invited, dock.Set<SavedCorporationApplication>().Find(invitation)!.Status);
            ship.StationId = deliveryStation;
            Assert.True(CapsuleerCorporations.Place(dock, catalog, fixture.Database, recruit, fixture.Clock.Now, due));
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
        Assert.Equal(
            CorporationApplicationStatus.Joined,
            Assert.Single(persisted.Applications(recruit, false), a => a.Id == invitation).Status
        );
        Assert.Equal(hiringCorporation, after.Identity(recruit).Id);
        Assert.Equal(botCorporation, after.Employment(recruit)[1].CorporationId);
        Assert.Equal(recruitShip, reopened.GetRequiredService<SqliteWorld>().FindPilot(-1, recruit)!.ShipId);
        Assert.Contains(persisted.Changes(recruiter, 0), change => change.Membership?.PilotId == recruit);
        await restored.FlushAsync();
    }

    private static bool CompleteRecruitment(
        EconomyTransaction world,
        CatalogDbContext catalog,
        CapsuleerHaulerTests.Fixture fixture,
        long pilot
    )
    {
        for (int review = 0; review < 3; review++)
        {
            long due = CapsuleerCorporations.Organization(world, pilot).DueTicks;
            if (CapsuleerCorporations.Place(world, catalog, fixture.Database, pilot, fixture.Clock.Now, due))
            {
                return true;
            }
        }
        return false;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PaidAdvertRecruitsTwoJumpOriginsAndOffersCompeteForLastBerth(bool recruiting)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PrepareExistingSave(fixture);
        const int headquarters = 31000001;
        int[] origins = [headquarters, 31000002, 31000003, 31000004];
        long[] recruits;
        int corporationId;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock);
            foreach (int origin in origins)
            {
                provisioner.EnsureSystem(origin);
            }
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var world = live.Economy.Begin(TransactionParticipants.None))
            {
                SavedCapsuleerAgent[] Agents(int system) =>
                    world
                        .Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == system)
                        .Where(agent => agent.Career == CapsuleerCareer.Hauling)
                        .OrderBy(agent => agent.PilotId)
                        .ToArray();
                long founder = Agents(headquarters)[0].PilotId;
                recruits = origins
                    .Select(origin =>
                        world
                            .Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == origin)
                            .First(agent => agent.Career == CapsuleerCareer.Mining)
                            .PilotId
                    )
                    .ToArray();
                foreach (long pilot in recruits.Prepend(founder))
                {
                    var ship = world.Pilots.Find(pilot)!.ActiveShip;
                    var agent = world.Set<SavedCapsuleerAgent>().Find(pilot)!;
                    ship.StationId = catalog.Definitions.StationsInSystem(agent.CommunitySystemId).First().Id;
                    if (world.Positions.Find(ship.Id) is { } position)
                    {
                        world.Remove(position);
                    }
                }
                Assert.True(CapsuleerCorporations.Place(world, catalog, fixture.Database, founder, fixture.Clock.Now));
                var company = Assert.Single(world.Set<SavedCorporation>());
                corporationId = company.Id;
                long fee = CorporationRecruitmentRules.Fee(1, true);
                company.BalanceCents = fee;
                long due = CapsuleerCorporations.Organization(world, founder).DueTicks;
                Assert.False(CapsuleerCorporations.Place(world, catalog, fixture.Database, founder, fixture.Clock.Now, due));
                Assert.Single(world.Set<SavedCorporationRecruitmentAd>());
                Assert.Equal(0, company.BalanceCents);
                Assert.True(CompleteRecruitment(world, catalog, fixture, recruits[0]));
                foreach (long recruit in recruits.Skip(1))
                {
                    Assert.False(CapsuleerCorporations.Place(world, catalog, fixture.Database, recruit, fixture.Clock.Now));
                    Assert.False(CapsuleerCorporations.Place(world, catalog, fixture.Database, recruit, fixture.Clock.Now));
                    var application = Assert.Single(world.Filter<SavedCorporationApplication>(a => a.PilotId == recruit));
                    Assert.Equal((company.Id, CorporationApplicationStatus.Applied), (application.CorporationId, application.Status));
                }
                world.Commit();
            }
            await live.FlushAsync();
        }
        if (!recruiting)
        {
            var configuration = new WorldConfigurationStore(fixture.Database, fixture.Clock);
            var state = configuration.Read();
            var draft = state.Active.Draft;
            configuration.Save(
                state.Generation,
                draft with
                {
                    Settings = draft.Settings with { Capsuleers = draft.Settings.Capsuleers with { AutomaticRecruitment = false } },
                },
                "Pause automatic recruitment"
            );
            WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        }
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        if (!recruiting)
        {
            var application = reopened.GetRequiredService<ICorporationMembership>().Apply(Human, corporationId, "Request to join");
            Assert.Equal(CorporationApplicationStatus.Applied, application.Status);
        }
        var restored = reopened.GetRequiredService<LiveWorld>();
        using CatalogDbContext source = new(fixture.Database.CatalogPath);
        using var admission = restored.Economy.Begin(TransactionParticipants.None);
        var charter = admission.Set<SavedCorporation>().Find(corporationId)!;
        foreach (long recruit in recruits.Skip(1))
        {
            long due = CapsuleerCorporations.Organization(admission, recruit).DueTicks;
            Assert.False(CapsuleerCorporations.Place(admission, source, fixture.Database, recruit, fixture.Clock.Now, due));
            Assert.Equal(
                CorporationApplicationStatus.Offered,
                Assert.Single(admission.Filter<SavedCorporationApplication>(a => a.PilotId == recruit)).Status
            );
        }
        charter.MemberLimit = 4;
        Assert.True(CompleteRecruitment(admission, source, fixture, recruits[1]));
        Assert.True(CompleteRecruitment(admission, source, fixture, recruits[2]));
        long rejectedDue = CapsuleerCorporations.Organization(admission, recruits[3]).DueTicks;
        Assert.False(CapsuleerCorporations.Place(admission, source, fixture.Database, recruits[3], fixture.Clock.Now, rejectedDue));
        Assert.Equal(corporationId, admission.Pilots.Find(recruits[1])!.CorporationId);
        Assert.Equal(corporationId, admission.Pilots.Find(recruits[2])!.CorporationId);
        Assert.NotEqual(corporationId, admission.Pilots.Find(recruits[3])!.CorporationId);
        Assert.Equal(
            CorporationApplicationStatus.RejectedByApplicant,
            Assert.Single(admission.Filter<SavedCorporationApplication>(a => a.PilotId == recruits[3])).Status
        );
        int[] originCounts = admission
            .Pilots.Where(pilot => pilot.CorporationId == corporationId)
            .AsEnumerable()
            .GroupBy(pilot => admission.Set<SavedCapsuleerAgent>().Find(pilot.Id)!.CommunitySystemId)
            .Select(group => group.Count())
            .OrderDescending()
            .ToArray();
        Assert.Equal([2, 1, 1], originCounts);
        var roster = Assert.IsType<CapsuleerRosterReport>(CapsuleerCorporations.Roster(admission, corporationId));
        Assert.Equal(originCounts, roster.Origins.Select(origin => origin.Pilots));
        Assert.Equal(0.5, roster.LargestOriginShare);
        Assert.Equal(recruiting ? 0 : 1, roster.PendingApplications);
        Assert.Equal(2, roster.PilotsRelocating);
    }

    [Fact]
    public async Task BetterEmployerRequiresTenureAndSustainedInterestThenRetainsFutureRebase()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PrepareExistingSave(fixture);
        int current;
        int next;
        long pilotId;
        long mature;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var world = live.Economy.Begin(TransactionParticipants.None))
            {
                var roster = world
                    .Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == Home)
                    .OrderBy(agent => agent.PilotId)
                    .Take(3)
                    .ToArray();
                long founder = roster[0].PilotId;
                pilotId = roster[1].PilotId;
                long recruiter = roster[2].PilotId;
                int berth = world.Pilots.Find(founder)!.ActiveShip.StationId;
                foreach (var agent in roster)
                {
                    var ship = world.Pilots.Find(agent.PilotId)!.ActiveShip;
                    ship.StationId = berth;
                    if (world.Positions.Find(ship.Id) is { } position)
                    {
                        world.Remove(position);
                    }
                }
                Assert.True(CapsuleerCorporations.Place(world, catalog, fixture.Database, founder, fixture.Clock.Now));
                Assert.True(CompleteRecruitment(world, catalog, fixture, pilotId));
                var incumbent = Assert.Single(world.Set<SavedCorporation>());
                current = incumbent.Id;
                incumbent.DetailsJson = System.Text.Json.JsonSerializer.Serialize(incumbent.Snapshot(0).Details with { TaxRate = 1.0 });
                world.Set<PilotWallet>().Find(recruiter)!.Cents = CorporationRules.CreationFeeCents;
                var skill =
                    world.Set<PilotSkill>().Find(recruiter, CorporationRules.ManagementSkill)
                    ?? world.Add(new PilotSkill { PilotId = recruiter, TypeId = CorporationRules.ManagementSkill }).Entity;
                skill.Points = 250;
                next = PlayerCorporationStore
                    .Create(
                        world,
                        catalog,
                        recruiter,
                        PlayerCorporationTests.Details() with
                        {
                            ApplicationsEnabled = true,
                            TaxRate = 0,
                        },
                        fixture.Clock.Now
                    )
                    .Corporation.Id;
                world.Set<SavedCorporation>().Find(next)!.CapsuleerFoundingSystemId = Home;
                var employment = Assert.IsType<CapsuleerEmployment>(CapsuleerCorporations.Organization(world, pilotId).Employment);
                mature = employment.JoinedTicks + CapsuleerRecruitmentRules.MinimumTenure.Ticks;
                CapsuleerCorporations.RememberWorkingContact(world, pilotId, next, mature);
                long due = CapsuleerCorporations.Organization(world, pilotId).DueTicks;
                Assert.True(due < mature);
                Assert.False(CapsuleerCorporations.Place(world, catalog, fixture.Database, pilotId, fixture.Clock.Now, due));
                Assert.Null(CapsuleerCorporations.Organization(world, pilotId).Prospect);
                using (var metrics = SessionPerformance.Begin("resident employer review", _ => { }, force: true)!)
                {
                    Assert.False(CapsuleerCorporations.Place(world, catalog, fixture.Database, pilotId, fixture.Clock.Now, mature));
                    Assert.Equal(0, metrics.Commands);
                    Assert.Equal(0, metrics.Connections);
                    Assert.Equal(0, metrics.FittingCalculations);
                }
                Assert.Equal(next, CapsuleerCorporations.Organization(world, pilotId).Prospect?.CorporationId);
                Assert.Null(CapsuleerCorporations.Organization(world, pilotId).ApplicationId);
                world.Commit();
            }
            await live.FlushAsync();
        }
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using CatalogDbContext definitions = new(fixture.Database.CatalogPath);
        var restored = reopened.GetRequiredService<LiveWorld>();
        using var review = restored.Economy.Begin(TransactionParticipants.None);
        long sustained = Math.Max(
            CapsuleerCorporations.Organization(review, pilotId).DueTicks,
            mature + CapsuleerRecruitmentRules.SustainedInterest.Ticks
        );
        Assert.False(CapsuleerCorporations.Place(review, definitions, fixture.Database, pilotId, fixture.Clock.Now, sustained));
        var invitation = Assert.Single(
            review.Filter<SavedCorporationApplication>(app => app.PilotId == pilotId).Where(app => app.CorporationId == next)
        );
        Assert.Equal(CorporationApplicationStatus.Invited, invitation.Status);
        Assert.Equal(current, review.Pilots.Find(pilotId)!.CorporationId);
        long accept = CapsuleerCorporations.Organization(review, pilotId).DueTicks;
        Assert.True(CapsuleerCorporations.Place(review, definitions, fixture.Database, pilotId, fixture.Clock.Now, accept));
        Assert.Equal(next, review.Pilots.Find(pilotId)!.CorporationId);
        Assert.Equal(CorporationApplicationStatus.Joined, invitation.Status);
        var corporation = review.Set<SavedCorporation>().Find(next)!;
        corporation.SystemId = 31000001;
        corporation.StationId = definitions.Definitions.StationsInSystem(corporation.SystemId).Single().Id;
        long rebaseReview = CapsuleerCorporations.Organization(review, pilotId).DueTicks;
        Assert.False(CapsuleerCorporations.Place(review, definitions, fixture.Database, pilotId, fixture.Clock.Now, rebaseReview));
        var move = Assert.IsType<CapsuleerRelocation>(CapsuleerCorporations.Organization(review, pilotId).Relocation);
        Assert.Equal((next, corporation.StationId), (move.CorporationId, move.DestinationId));
    }

    [Theory]
    [InlineData(CapsuleerCareer.Mining)]
    [InlineData(CapsuleerCareer.Hauling)]
    public async Task RecruitsFlyOwnedShipsAndSuppliesToTheirBaseAcrossCheckpoint(CapsuleerCareer career)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PrepareExistingSave(fixture);
        const int origin = 31000001;
        const int distantObserver = 31000004;
        const int supplies = 13;
        const int charge = CapsuleerAmmunitionSupplyRules.ChargeType;
        long recruit;
        long ship;
        int source;
        int destination;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock);
            provisioner.EnsureSystem(origin);
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var world = live.Economy.Begin(TransactionParticipants.None))
            {
                long founder = world
                    .Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == Home)
                    .First(agent => agent.Career == CapsuleerCareer.Hauling)
                    .PilotId;
                recruit = world
                    .Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == origin)
                    .First(agent => agent.Career == career)
                    .PilotId;
                var pilot = world.Pilots.Find(recruit)!;
                ship = pilot.ActiveShipId;
                source = catalog.Definitions.StationsInSystem(origin).First().Id;
                pilot.ActiveShip.StationId = source;
                if (world.Positions.Find(ship) is { } position)
                {
                    world.Remove(position);
                }
                if (career == CapsuleerCareer.Hauling)
                {
                    new HaulerCheckpoint(recruit, new(HaulerActivity.Docked, 0, 0, ship, source, 0, new NavigationState().Capture())).Apply(
                        world
                    );
                    world.Set<SavedCapsuleerJob>().Find(recruit)!.Action = CapsuleerAction.Trade;
                }
                InventoryPersistence.MergeOutput(world, catalog, recruit, source, (int)InventoryFlag.Hangar, charge, supplies);
                Assert.True(CapsuleerCorporations.Place(world, catalog, fixture.Database, founder, fixture.Clock.Now));
                Assert.True(CompleteRecruitment(world, catalog, fixture, recruit));
                destination = Assert
                    .IsType<CapsuleerRelocation>(CapsuleerCorporations.Organization(world, recruit).Relocation)
                    .DestinationId;
                Assert.NotEqual(source, destination);
                foreach (
                    long other in world.Set<SavedCapsuleerAgent>().Where(agent => agent.PilotId != recruit).Select(agent => agent.PilotId)
                )
                {
                    if (world.Set<SavedCapsuleerSuspension>().Find(other) is null)
                    {
                        world.Add(new SavedCapsuleerSuspension { PilotId = other });
                    }
                }
                world.Commit();
            }
            var presence = host.GetRequiredService<IHumanPresence>();
            using var observer = presence.Select(Human, distantObserver);
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            for (int second = 0; second < 300 && live.Economy.Peek<FlightPosition>(ship) is null; second++)
            {
                fixture.Clock.Seconds++;
                maintenance.Tick();
            }
            Assert.Equal(origin, live.Economy.Peek<FlightPosition>(ship)?.SystemId);
            var moving = live.Economy.Read(world => CapsuleerCorporations.Organization(world, recruit));
            Assert.Equal(ship, moving.Relocation?.ShipId);
            Assert.Null(moving.OperatingStationId);
            Assert.Single(moving.Relocation!.Cargo, item => item.Quantity == supplies);
            Assert.False(live.IsDetailedSystem(origin));
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        var run = reopened.GetRequiredService<CapsuleerMaintenance>();
        fixture.Clock.Seconds += 1000;
        Assert.Null(run.Tick());
        Assert.Equal(origin, restored.Economy.Peek<FlightPosition>(ship)?.SystemId);
        using var remote = reopened.GetRequiredService<IHumanPresence>().Select(Human, Home);
        for (int second = 0; second < 900; second++)
        {
            fixture.Clock.Seconds++;
            run.Tick();
            if (restored.Economy.Read(world => CapsuleerCorporations.Organization(world, recruit).OperatingStationId) == destination)
            {
                break;
            }
        }
        var diagnostics = reopened.GetRequiredService<CapsuleerScheduler>().Read();
        Assert.True(diagnostics.Failures == 0, diagnostics.LastFailure);
        restored.Economy.Read(world =>
        {
            var organization = CapsuleerCorporations.Organization(world, recruit);
            Assert.Equal(destination, organization.OperatingStationId);
            Assert.Null(organization.Relocation);
            Assert.Equal(1, organization.RelocationsCompleted);
            Assert.Equal(ship, world.Pilots.Find(recruit)!.ActiveShipId);
            Assert.Equal(destination, world.Ships.Find(ship)!.StationId);
            Assert.Equal(origin, world.Set<SavedCapsuleerAgent>().Find(recruit)!.CommunitySystemId);
            Assert.Equal(
                supplies,
                world
                    .Filter<OwnedItem>(item => item.OwnerId == recruit)
                    .Where(item => item.LocationId == destination && item.TypeId == charge)
                    .Sum(item => item.Quantity)
            );
            Assert.Empty(
                world.Filter<OwnedItem>(item => item.OwnerId == recruit).Where(item => item.LocationId == source && item.TypeId == charge)
            );
            return true;
        });
        bool StartedAtBase() =>
            career == CapsuleerCareer.Mining
                ? restored.MinerCheckpoints.GetValueOrDefault(recruit)?.Work is { Trips: > 0, OperatingStationId: var station }
                    && station == destination
                : restored.HaulerCheckpoints.GetValueOrDefault(recruit)?.Work.Journey is { HomeStationId: var home } && home == destination;
        for (int second = 0; second < 900 && !StartedAtBase(); second++)
        {
            fixture.Clock.Seconds++;
            run.Tick();
        }
        Assert.True(StartedAtBase());
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
