using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Skills;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task KillAddsToTheSavedPoolAndNotifiesOnlyTheRecipientOnce(bool resident)
    {
        using PilotCombatTests.Fixture f = new();
        SetBounty(f, 150000);
        f.AlmostDefeated();
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
        handler.Enter(context);
        var points = f.Db.Services.GetRequiredService<IPilotFreeSkillPoints>();
        Assert.Equal(new(123, 4), points.Observe(Pilot));

        var shot = f.Fire();
        Assert.True(shot.Destroyed);
        Assert.Equal(15000000, shot.Reward!.AmountCents);
        Assert.Equal(15000000, shot.Reward.BalanceCents);
        Assert.Equal(new(498, 5), points.Observe(Pilot));
        Assert.Equal(0, points.Balance(90000002));
        handler.Advance(context);
        var notification = Assert.Single(context.Notifications.OfType<FreeSkillPointsChanged>());
        Assert.Equal(498, notification.Balance);
        Assert.Equal(new object?[] { 498L }, BlueMarshal.Decode(BlueMarshal.Encode(notification.Arguments)));
        f.Clock.Milliseconds += 1000;
        handler.Advance(context);
        Assert.Single(context.Notifications.OfType<FreeSkillPointsChanged>());
        Assert.Throws<InvalidDataException>(() => f.Fire());
        Assert.Equal(new(498, 5), points.Observe(Pilot));

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
        Assert.Equal(5, saved.Revision);
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

    [Theory]
    [InlineData(false, long.MaxValue, 0)]
    [InlineData(true, long.MaxValue, 0)]
    [InlineData(false, 0, long.MaxValue)]
    [InlineData(true, 0, long.MaxValue)]
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
        if (revision == long.MaxValue)
        {
            Assert.Throws<OverflowException>(() => f.Fire());
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => f.Fire());
        }
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
