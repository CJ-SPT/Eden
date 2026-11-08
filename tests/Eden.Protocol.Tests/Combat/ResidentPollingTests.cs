using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Redemption;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class ResidentPollingTests
{
    [Fact]
    public async Task RedeemPollingInvalidatesClaimsTrashAndReversalsAndSurvivesCheckpoint()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var store = db.Services.GetRequiredService<IAccountRedemption>();
        var first = store.Grant(new(1000001, "claim", 601, 1, "Reward"));
        var second = store.Grant(new(1000001, "trash", 601, 1, "Reward"));
        Assert.Equal(2, store.Read(1000001).Count);
        var claimed = store.Claim(1000001, 90000001, [new(first.Id, first.TypeId)], false);
        var item = Assert.Single(claimed.Items).Item;
        Assert.Equal(second, Assert.Single(store.Read(1000001)));
        Assert.Throws<InvalidDataException>(() => store.Trash(1000001, [second.Id, long.MaxValue]));
        Assert.Equal(second, Assert.Single(store.Read(1000001)));
        store.Trash(1000001, [second.Id]);
        Assert.Empty(store.Read(1000001));
        store.Reverse(1000001, 90000001, item.Id);
        var reversed = Assert.Single(store.Read(1000001));
        Assert.NotEqual(first.Id, reversed.Id);
        var owner = Eden.World.Runtime.LiveWorld.Find(db.Path)!;
        await owner.FlushAsync();
        string reopened = Path.Combine(Path.GetDirectoryName(db.Path)!, "polling-checkpoint.sqlite");
        File.Copy(db.Path, reopened);
        using var saved = Eden.Server.ServerServices.Build(new(reopened), clock);
        Assert.Equal(reversed, Assert.Single(saved.GetRequiredService<IAccountRedemption>().Read(1000001)));
    }

    [Fact]
    public void WalletPollingRechecksTitlesRolesMembershipAndDivisionBalances()
    {
        using TestWorld db = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        const long member = 90000002;
        var wallet = db.Services.GetRequiredService<ICorporationWallet>();
        Assert.Empty(wallet.Divisions(member));
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new SavedCorporationMemberAccess
                {
                    PilotId = member,
                    CorporationId = corporation,
                    DataJson = JsonSerializer.Serialize(new CorporationMemberAccess(new(), TitleMask: 1)),
                }
            );
            world.SaveChanges();
        }
        Assert.Empty(wallet.Divisions(member));
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new SavedCorporationTitle
                {
                    CorporationId = corporation,
                    TitleId = 1,
                    DataJson = JsonSerializer.Serialize(new CorporationTitle(1, "Accounts", new((long)CorporationRole.Accountant))),
                }
            );
            world.Add(
                new SavedCorporationWallet
                {
                    CorporationId = corporation,
                    Key = CorporationWalletRules.LastKey,
                    BalanceCents = 123,
                }
            );
            world.SaveChanges();
        }
        var divisions = wallet.Divisions(member);
        Assert.Equal(CorporationWalletRules.DivisionCount, divisions.Count);
        Assert.Equal(123, divisions.Single(d => d.Key == CorporationWalletRules.LastKey).BalanceCents);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<SavedCorporationWallet>().Find(corporation, CorporationWalletRules.LastKey)!.BalanceCents = 456;
            world.SaveChanges();
        }
        Assert.Equal(456, wallet.Divisions(member).Single(d => d.Key == CorporationWalletRules.LastKey).BalanceCents);
        Assert.Equal(123, divisions.Single(d => d.Key == CorporationWalletRules.LastKey).BalanceCents);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<SavedCorporationMemberAccess>().Find(member)!.DataJson = JsonSerializer.Serialize(
                new CorporationMemberAccess(new(), TitleMask: 1, BlockRoles: true)
            );
            world.SaveChanges();
        }
        Assert.Empty(wallet.Divisions(member));
        using (WorldDbContext world = new(db.Path))
        {
            world.Pilots.Find(member)!.CorporationId = 1000044;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => wallet.Divisions(member));
    }

    [Fact]
    public void WarmPollingReadsAvoidSqlAndInvalidateAfterCommittedChanges()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        const long pilot = 90000001;
        const long wing = 90000002;
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        var redemption = db.Services.GetRequiredService<IAccountRedemption>();
        var wallet = db.Services.GetRequiredService<ICorporationWallet>();
        contacts.Add(pilot, wing, 5, true);
        var original = contacts.Read(pilot);
        Assert.Empty(redemption.Read(1000001));
        var balances = wallet.Divisions(pilot);
        using (var metrics = SessionPerformance.Begin("warm polling", _ => { }, force: true)!)
        {
            for (int repeat = 0; repeat < 20; repeat++)
            {
                clock.Seconds++;
                Assert.Same(original, contacts.Read(pilot));
                Assert.Empty(redemption.Read(1000001));
                Assert.Equal(balances, wallet.Divisions(pilot));
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        contacts.SetBlocked(pilot, [wing], true);
        Assert.Equal(wing, Assert.Single(contacts.Read(pilot).Blocked));
        Assert.Empty(original.Blocked);
        contacts.Edit(pilot, wing, -5, false);
        Assert.Equal(new PersonalContact(wing, -5, false, 0), Assert.Single(contacts.Read(pilot).Addresses));
        using (WorldDbContext world = new(db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Set<SavedContact>().Single(c => c.PilotId == pilot).Standing = 10;
            world.Set<SavedCorporation>().Find(corporation)!.BalanceCents = 123;
            world.SaveChanges();
            transaction.Rollback();
        }
        Assert.Equal(-5, Assert.Single(contacts.Read(pilot).Addresses).Standing);
        Assert.Equal(balances, wallet.Divisions(pilot));
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<SavedCorporation>().Find(corporation)!.BalanceCents = 456;
            world.SaveChanges();
        }
        Assert.Equal(456, wallet.Divisions(pilot).Single(d => d.Key == CorporationWalletRules.MasterKey).BalanceCents);
        var token = redemption.Grant(new(1000001, "polling", 601, 1, "Reward", ExpiresAt: clock.Now + TimeSpan.TicksPerSecond));
        var pending = redemption.Read(1000001);
        Assert.Equal(token, Assert.Single(pending));
        clock.Milliseconds += 999;
        Assert.Single(redemption.Read(1000001));
        clock.Milliseconds++;
        Assert.Empty(redemption.Read(1000001));
        Assert.Single(pending);
        Assert.Throws<InvalidDataException>(() => contacts.Read(123));
        Assert.Throws<InvalidDataException>(() => redemption.Read(123));
    }
}
