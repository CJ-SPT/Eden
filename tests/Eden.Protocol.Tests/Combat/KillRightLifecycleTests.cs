using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Notifications;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class KillRightLifecycleTests
{
    private const long Owner = 90000001;
    private const long Target = 90000002;
    private const long OwnerShip = 1000000000001;
    private const long TargetShip = 1000000000002;
    private const int System = 30000142;

    [Fact]
    public async Task RestrictedPaidActivationAndUseSettleAtomicallyAcrossCheckpoints()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock);
        WorldDatabase database = new(fixture.Path);
        TestCatalog.AddSchool(database.CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        AddCapsule(database.CatalogPath);
        using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (186,'Wreck',2);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Capacity,Mass,Radius) VALUES (26510,'Rookie Wreck',186,27500,10000,14);
                INSERT OR IGNORE INTO SourceRecord VALUES ('client/types','26510',json_object('published',1,'volume',27500));
                UPDATE SourceRecord SET Data=json_set(Data,'$.wreckTypeID',26510) WHERE Dataset='client/types' AND Key='601';
                """
            );
        }
        var services = fixture.Services;
        var rights = services.GetRequiredService<IPilotKillRights>();
        var wallet = services.GetRequiredService<IPilotWalletReader>();
        long buyer,
            buyerShip,
            id;
        using (LiveWorld live = new(database, clock, _ => { }))
        {
            buyer = services.GetRequiredService<ICharacterCreation>().Create(1000001, new("Retribution Buyer", 1, 1, 1, 64, [1], [2], 18));
            buyerShip = live.Economy.Inspect(world => world.Pilots.Find(buyer)!.ActiveShipId);
            fixture.Store.SetPosition(OwnerShip, new(10_000_000, 0, 0));
            fixture.Store.SetPosition(TargetShip, new(10_001_000, 0, 0));
            fixture.Store.SetPosition(buyerShip, new(10_000_500, 0, 0), System);
            SetWallet(live, buyer, 0);
            Earn(live, clock);
            id = Assert.Single(rights.Mine(Owner)).Id;
            Assert.Equal(id, Assert.Single(rights.Mine(Target)).Id);
            Assert.Empty(rights.Mine(buyer));
            Assert.Empty(rights.Available(buyer, [Target]));

            using var scope = services.CreateScope();
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            var flights = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
            var pilots = services.GetRequiredService<Eden.Gameplay.Docking.IPilotLookup>().ListPilots(1000001);
            var pilot = pilots.Single(p => p.PilotId == Owner);
            using PilotCallContext context = new(1000001, pilot.Account, [pilot], flights.Create, 10, SignedSettings.Empty);
            context.Select(pilot);
            var buyerPilot = pilots.Single(p => p.PilotId == buyer);
            using PilotCallContext buyerContext = new(1000001, buyerPilot.Account, [buyerPilot], flights.Create, 11, SignedSettings.Empty);
            buyerContext.Select(buyerPilot);
            var listed = Assert.IsType<List<object?>>(
                router.CallWire(context, "bountyProxy", "SellKillRight", new object?[] { id, 5L, buyer }, _ => { })
            );
            var fields = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(Assert.Single(listed)).State);
            Assert.Equal(5d, fields["price"]);
            Assert.Equal(buyer, fields["restrictedTo"]);
            Assert.Equal(id, Assert.Single(rights.Available(buyer, [Target, Target])).Id);
            Assert.Empty(rights.Available(Target, [Target]));
            var combined = Assert.IsType<object?[]>(
                router.CallWire(
                    buyerContext,
                    "bountyProxy",
                    "GetBountiesAndKillRights",
                    new object?[]
                    {
                        new HashSet<long> { Target },
                        new List<object?> { Target },
                    },
                    _ => { }
                )
            );
            Assert.Single(Assert.IsType<List<object?>>(combined[0]));
            Assert.Single(Assert.IsType<List<object?>>(combined[1]));
            Assert.Throws<InvalidDataException>(() => rights.CancelSale(buyer, id, Target));
            Assert.Throws<InvalidDataException>(() => rights.CancelSale(Owner, id, buyer));

            var presence = new KillRightPresence(System, buyerShip, new HashSet<long> { TargetShip });
            Assert.Equal(KillRightActivationResult.Unavailable, rights.Activate(buyer, id, presence));
            Assert.Equal(
                "NoValidKillRight",
                Assert
                    .IsType<PilotCallResult.NamedFailure>(
                        router.Call(buyerContext, "killRightMgr", "BuyKillRight", new object?[] { id, 4.99 }, _ => { })
                    )
                    .Name
            );
            Assert.Equal(
                KillRightActivationResult.TargetNotVisible,
                rights.Activate(buyer, id, presence with { VisibleShips = new HashSet<long>() }, 500)
            );
            Assert.Throws<InvalidDataException>(() => rights.Activate(buyer, id, presence, 500));
            Assert.Equal(CrimewatchFlag.None, services.GetRequiredService<IPilotCrimewatch>().Read(Target, System).Timers.Flag(clock.Now));
            SetWallet(live, buyer, 1_000);

            var attempts = await Task.WhenAll(
                Task.Run(() => rights.Activate(buyer, id, presence, 500)),
                Task.Run(() => rights.Activate(buyer, id, presence, 500))
            );
            Assert.Contains(KillRightActivationResult.Unavailable, attempts);
            Assert.Contains(KillRightActivationResult.Activated, attempts);
            Assert.Equal(500, wallet.Balance(buyer));
            Assert.Equal(500, wallet.Balance(Owner));
            Assert.Empty(rights.Available(buyer, [Target]));
            var journal = services.GetRequiredService<ICorporationWallet>().Journal(buyer, false, CorporationWalletRules.MasterKey);
            var payment = Assert.Single(journal, row => row.EntryType == WalletEntryType.KillRightPurchase);
            Assert.Equal(-500, payment.AmountCents);
            Assert.Equal(id, payment.ReferenceId);
            await live.FlushAsync();
        }

        using (LiveWorld restored = new(database, clock, _ => { }))
        {
            Assert.Equal(id, Assert.Single(rights.Mine(Owner)).Id);
            Assert.Equal(500, wallet.Balance(buyer));
            var watch = services.GetRequiredService<IPilotCrimewatch>().Read(Target, System);
            Assert.Equal(CrimewatchFlag.Suspect, watch.Timers.Flag(clock.Now));
            Assert.Equal(clock.Now + CrimewatchRules.FlagDuration.Ticks, watch.Timers.Suspect.ExpiresAt);
            using (var world = restored.Economy.Begin(new(pilots: [buyer, Target])))
            using (CatalogDbContext catalog = new(database.CatalogPath))
            {
                var target = world.Pilots.Find(Target)!;
                var attacker = world.Pilots.Find(buyer)!;
                services
                    .GetRequiredService<PilotLossSettlement>()
                    .Apply(
                        world,
                        catalog,
                        target,
                        TargetShip,
                        System,
                        target.ActiveShip.Position!,
                        clock.Now,
                        new(buyerShip, buyer, attacker.CorporationId, null, attacker.ActiveShip.TypeId, null, null, 100)
                    );
                world.Commit();
            }
            Assert.Empty(rights.Mine(Owner));
            var report = Assert.Single(services.GetRequiredService<IPilotInteractionHistory>().Combat(buyer, 10, null));
            Assert.Equal(Owner, report.KillRightSupplierId);
            var notices = services.GetRequiredService<IPilotInbox>().Read(Owner, new(Group: InboxGroup.Miscellaneous));
            Assert.Equal(new[] { InboxNotificationType.KillRightUsed, InboxNotificationType.KillRightEarned }, notices.Select(n => n.Type));
            await restored.FlushAsync();
        }

        using LiveWorld final = new(database, clock, _ => { });
        Assert.Empty(rights.Mine(Owner));
        Assert.Equal(500, wallet.Balance(buyer));
    }

    [Fact]
    public async Task CancelledSaleAndExactExpiryKeepActivationAndAwardRetrySafe()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock);
        WorldDatabase database = new(fixture.Path);
        var rights = fixture.Services.GetRequiredService<IPilotKillRights>();
        long replacement;
        using (LiveWorld live = new(database, clock, _ => { }))
        {
            fixture.Store.SetPosition(OwnerShip, new(10_000_000, 0, 0));
            fixture.Store.SetPosition(TargetShip, new(10_001_000, 0, 0));
            Earn(live, clock, CrimewatchFlag.Suspect);
            Assert.Empty(rights.Mine(Owner));
            Earn(live, clock);
            var first = Assert.Single(rights.Mine(Owner));
            Earn(live, clock);
            Assert.Equal(first, Assert.Single(rights.Mine(Owner)));
            Assert.Equal(0, rights.ListForSale(Owner, first.Id, 0, null).PriceCents);
            Assert.Single(rights.Available(Target, [Target]));
            rights.CancelSale(Owner, first.Id, Target);
            rights.CancelSale(Owner, first.Id, Target);
            Assert.Empty(rights.Available(Target, [Target]));

            var presence = new KillRightPresence(System, OwnerShip, new HashSet<long> { TargetShip });
            using var scope = fixture.Services.CreateScope();
            var flights = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
            using var owner = Connect(fixture, flights, 1000001);
            using var target = Connect(fixture, flights, 1000002);
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            Assert.Null(router.CallWire(owner, "killRightMgr", "ActivateKillRight", new object?[] { first.Id }, _ => { }));
            Assert.Single(owner.Notifications.OfType<KillRightActivated>());
            clock.AdvanceWallTime(CrimewatchRules.FlagDuration);
            Assert.Equal(KillRightActivationResult.Activated, rights.Activate(Owner, first.Id, presence));
            clock.AdvanceWallTime(KillRightRules.Lifetime - CrimewatchRules.FlagDuration);
            Assert.Empty(rights.Mine(Owner));
            Assert.Equal(KillRightActivationResult.Expired, rights.Activate(Owner, first.Id, presence));
            await live.FlushAsync();

            Earn(live, clock);
            replacement = Assert.Single(rights.Mine(Owner)).Id;
            Assert.True(replacement > first.Id);
            await live.FlushAsync();
        }
        using LiveWorld reopened = new(database, clock, _ => { });
        Assert.Equal(replacement, Assert.Single(rights.Mine(Owner)).Id);
    }

    private static void Earn(LiveWorld live, TestClock clock, CrimewatchFlag flag = CrimewatchFlag.Criminal)
    {
        using var world = live.Economy.Begin(TransactionParticipants.None);
        PilotKillRightStore.Earn(world, Owner, Target, new(flag, false), clock.Now);
        world.Commit();
    }

    private static PilotCallContext Connect(TestWorld world, FlightSessionFactory flights, long account)
    {
        var pilot = world.Store.FindPilot(account)!;
        PilotCallContext context = new(account, pilot.Account, [pilot], flights.Create, account, SignedSettings.Empty);
        context.Select(pilot);
        context.Flight.Snapshot();
        return context;
    }

    private static void SetWallet(LiveWorld live, long pilot, long balance)
    {
        using var world = live.Economy.Begin(TransactionParticipants.None);
        var wallet = world.Set<PilotWallet>().Find(pilot) ?? world.Add(new PilotWallet { PilotId = pilot }).Entity;
        wallet.Cents = balance;
        world.Commit();
    }

    internal static void AddCapsule(string catalogPath)
    {
        using CatalogDbContext catalog = new(catalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (29,'Capsule',6);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Capacity,Mass,Radius) VALUES (670,'Capsule',29,0,32000,2);
            INSERT OR IGNORE INTO TypeAttribute SELECT 670,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=601;
            """
        );
    }
}
