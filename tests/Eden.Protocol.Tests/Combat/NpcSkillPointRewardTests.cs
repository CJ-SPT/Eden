using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcSkillPointRewardTests
{
    private const long Pilot = 90000001;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(39999, 0)]
    [InlineData(40000, 1)]
    [InlineData(40001, 1)]
    [InlineData(300000, 7)]
    [InlineData(15000000, 375)]
    [InlineData(15039999, 375)]
    [InlineData(15040000, 376)]
    [InlineData(long.MaxValue, 230584300921369)]
    public void GrossBountyAwardsWholePointsAtOneQuarterPercent(long cents, long expected) =>
        Assert.Equal(expected, NpcSkillPointRewards.FromBounty(cents));

    [Fact]
    public void NegativeBountyIsRejected() => Assert.Throws<InvalidDataException>(() => NpcSkillPointRewards.FromBounty(-1));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.25, 50000)]
    [InlineData(0.1, 20000)]
    [InlineData(1, 200000)]
    [InlineData(100, 20000000)]
    public void ConfiguredBasePercentageCombinesWithTheSkillPointMultiplier(double percent, long expected) =>
        Assert.Equal(expected, NpcSkillPointRewards.FromBounty(100_000_000, 20, (decimal)percent));

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public void UnsupportedBasePercentageIsRejected(double percent) =>
        Assert.Throws<InvalidDataException>(() => NpcSkillPointRewards.FromBounty(100_000_000, 20, (decimal)percent));

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, 0)]
    public async Task KillAddsToTheSavedPoolWithoutSkillPointNotifications(bool resident, int? interval)
    {
        using PilotCombatTests.Fixture f = new(new TestClock());
        SetBounty(f, 150000);
        f.AlmostDefeated();
        if (interval is int seconds)
        {
            WorldTuningTests.Activate(f.Db, settings => settings with { Rewards = settings.Rewards with { BountyTickSeconds = seconds } });
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new SavedFreeSkillPoints
                {
                    PilotId = Pilot,
                    Points = 123,
                    Revision = 4,
                }
            );
            world.SaveChanges();
        }
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        var (context, targeting, _) = f.Client();
        using var owned = context;
        using var locks = targeting;
        using var scope = f.Db.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<FreeSkillPointHandler>();
        var walletMonitor = scope.ServiceProvider.GetRequiredService<WalletBalanceMonitor>();
        handler.Enter(context);
        walletMonitor.Enter(context);
        var points = f.Db.Services.GetRequiredService<IPilotFreeSkillPoints>();
        Assert.Equal(new(123, 4), points.Observe(Pilot));

        var shot = f.Fire();
        Assert.True(shot.Destroyed);
        var wallet = f.Db.Services.GetRequiredService<IPilotWalletReader>();
        if (resident && interval != 0)
        {
            Assert.Null(shot.Reward);
            Assert.Empty(wallet.Journal(Pilot).Rewards);
            Assert.Equal(new(123, 4), points.Observe(Pilot));
            var ticks = f.Db.Services.GetRequiredService<IActivityRewardTicks>();
            f.Clock.Milliseconds += ActivityRewardTickRules.DefaultIntervalSeconds * 1000 - 1;
            ticks.Advance();
            Assert.Empty(wallet.Journal(Pilot).Rewards);
            Assert.Equal(new(123, 4), points.Observe(Pilot));
            f.Clock.Milliseconds++;
            ticks.Advance();
            ticks.Advance();
        }
        else
        {
            Assert.NotNull(shot.Reward);
        }
        var reward = Assert.Single(wallet.Journal(Pilot).Rewards);
        Assert.Equal(15000000, reward.AmountCents);
        Assert.Equal(15000000, reward.BalanceCents);
        Assert.Equal(new(498, 4), points.Observe(Pilot));
        Assert.Equal(0, points.Balance(90000002));
        handler.Advance(context);
        Assert.Empty(context.Notifications);
        var balanceReply = Assert.IsType<PilotCallResult.Success>(handler.Call(context, "@skills", "GetFreeSkillPoints", []));
        Assert.Equal(498L, BlueMarshal.Decode(BlueMarshal.Encode(balanceReply.Reply)));
        var queueReply = Assert.IsType<PilotCallResult.Success>(handler.Call(context, "@skills", "GetSkillQueueAndFreePoints", []));
        Assert.Equal(498, Assert.IsType<SkillQueueReply>(queueReply.Reply).FreePoints);
        Assert.Empty(context.Notifications);
        f.Clock.Milliseconds += 1000;
        handler.Advance(context);
        Assert.Empty(context.Notifications);
        Assert.Throws<InvalidDataException>(() => f.Fire());
        Assert.Equal(new(498, 4), points.Observe(Pilot));
        if (resident && interval != 0)
        {
            walletMonitor.Advance(context);
            Assert.Equal(new AccountBalanceChanged(Pilot, 150000), Assert.Single(context.Notifications));
        }

        if (live is not null)
        {
            await live.FlushAsync();
            live.Dispose();
        }
        _ = WorldInitialization.Open(f.Db.Path);
        using WorldDbContext reopened = new(f.Db.Path);
        var saved = Assert.Single(reopened.Set<SavedFreeSkillPoints>());
        Assert.Equal(Pilot, saved.PilotId);
        Assert.Equal(498, saved.Points);
        Assert.Equal(4, saved.Revision);
        Assert.Single(reopened.Set<NpcBounty>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(399.99)]
    public void KillsBelowOneWholePointDoNotCreateAPool(double bounty)
    {
        using PilotCombatTests.Fixture f = new();
        SetBounty(f, bounty);
        f.AlmostDefeated();
        Assert.True(f.Fire().Destroyed);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedFreeSkillPoints>());
    }

    [Fact]
    public async Task MiningOpensTheSharedTickAndLateSettlementKeepsPilotDeadlinesSeparate()
    {
        using PilotCombatTests.Fixture f = new(new TestClock());
        const long fleetmate = 90000002;
        const int interval = 5;
        WorldTuningTests.Activate(f.Db, settings => settings with { Rewards = settings.Rewards with { BountyTickSeconds = interval } });
        var ticks = f.Db.Services.GetRequiredService<IActivityRewardTicks>();
        var wallet = f.Db.Services.GetRequiredService<IPilotWalletReader>();
        var points = f.Db.Services.GetRequiredService<IPilotFreeSkillPoints>();

        using (var live = new LiveWorld(new(f.Db.Path), f.Clock))
        {
            using (var world = live.Economy.Begin(new(pilots: [Pilot])))
            {
                ActivityRewardTickStore.RecordMining(world, Pilot, 9, f.Clock.Now);
                world.Commit();
            }
            f.Clock.Seconds += interval - 1;
            using (var world = live.Economy.Begin(new(pilots: [Pilot, fleetmate])))
            {
                var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
                ActivityRewardTickStore.RecordBounty(world, world.Pilots.Find(Pilot)!, npc, 150000, 7, f.Clock.Now);
                ActivityRewardTickStore.RecordBounty(world, world.Pilots.Find(Pilot)!, npc, 360000, 18, f.Clock.Now);
                ActivityRewardTickStore.RecordBounty(world, world.Pilots.Find(fleetmate)!, npc, 150000, 0, f.Clock.Now);
                world.Commit();
            }
            ticks.Advance();
            Assert.Empty(wallet.Journal(Pilot).Rewards);
            Assert.Equal(0, points.Balance(Pilot));

            f.Clock.Seconds++;
            using (var world = live.Economy.Begin(new(pilots: [Pilot])))
            {
                ActivityRewardTickStore.RecordMining(world, Pilot, 3, f.Clock.Now);
                world.Commit();
            }
            ticks.Advance();
            Assert.Equal(510000, Assert.Single(wallet.Journal(Pilot).Rewards).AmountCents);
            Assert.Equal(34, points.Balance(Pilot));
            Assert.Empty(wallet.Journal(fleetmate).Rewards);
            await live.FlushAsync();
        }

        using var reopened = new LiveWorld(new(f.Db.Path), f.Clock);
        f.Clock.Seconds += interval;
        ticks.Advance();
        ticks.Advance();
        Assert.Single(wallet.Journal(Pilot).Rewards);
        Assert.Equal(37, points.Balance(Pilot));
        Assert.Equal(150000, Assert.Single(wallet.Journal(fleetmate).Rewards).AmountCents);
        Assert.Equal(0, points.Balance(fleetmate));
    }

    [Theory]
    [InlineData(false, long.MaxValue, 0)]
    [InlineData(true, long.MaxValue, 0)]
    [InlineData(false, -1, 0)]
    [InlineData(true, -1, 0)]
    [InlineData(false, 0, -1)]
    [InlineData(true, 0, -1)]
    public void FailedPointCreditRollsBackTheEntireKill(bool resident, long balance, long revision)
    {
        using PilotCombatTests.Fixture f = new();
        f.AlmostDefeated();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new SavedFreeSkillPoints
                {
                    PilotId = Pilot,
                    Points = balance,
                    Revision = revision,
                }
            );
            world.SaveChanges();
        }
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        var points = f.Db.Services.GetRequiredService<IPilotFreeSkillPoints>();
        if (balance >= 0 && revision >= 0)
        {
            Assert.Equal(new(balance, revision), points.Observe(Pilot));
        }
        Assert.Throws<InvalidDataException>(() => f.Fire());
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Set<WorldNpc>().Find(f.Npc.Id)!.DestroyedAt);
        Assert.Equal(19, saved.Set<WorldNpc>().Find(f.Npc.Id)!.StructureDamage);
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<WorldWreck>());
        Assert.Empty(saved.Set<ShipEnergy>());
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Empty(saved.Set<PilotWallet>());
        var unchanged = Assert.Single(saved.Set<SavedFreeSkillPoints>());
        Assert.Equal(balance, unchanged.Points);
        Assert.Equal(revision, unchanged.Revision);
        if (balance >= 0 && revision >= 0)
        {
            Assert.Equal(new(balance, revision), points.Observe(Pilot));
        }
    }

    private static void SetBounty(PilotCombatTests.Fixture f, double bounty)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSql($"UPDATE TypeAttribute SET Value={bounty} WHERE ItemTypeId=30380 AND AttributeId=481");
    }
}
