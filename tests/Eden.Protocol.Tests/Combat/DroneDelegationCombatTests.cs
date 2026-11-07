using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class DroneDelegationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewDelegatedAttacksRespectOwnerControlRangeAndDoNotReplayAnOutOfRangeAction(bool resident)
    {
        using Fixture fixture = new(resident);
        fixture.Assign(DroneDelegationMode.Assist);
        fixture.Base.Base.Db.Store.SetPosition(MemberShip, fixture.Base.Npc.Position + new SpacePosition(55000, 0, 0), SystemId);
        fixture.PublishMember();
        using (WorldDbContext world = new(fixture.Base.Base.Db.Path))
        {
            world.Set<WorldNpc>().Find(fixture.Base.Npc.Id)!.X = fixture.Base.Npc.Position.X + 70000;
            world.SaveChanges();
        }
        fixture.Fire();
        Assert.Empty(fixture.Base.Tick().Hits);
        Assert.All(fixture.Contacts(), d => Assert.Equal(DroneActivity.Idle, d.Activity));
        using (WorldDbContext world = new(fixture.Base.Base.Db.Path))
        {
            world.Set<WorldNpc>().Find(fixture.Base.Npc.Id)!.X = fixture.Base.Npc.Position.X;
            world.SaveChanges();
        }
        Assert.Empty(fixture.Base.Tick(10).Hits);
        fixture.Base.Base.Db.Store.SetPosition(MemberShip, fixture.Base.Npc.Position + new SpacePosition(1500, 0, 0), SystemId);
        fixture.PublishMember();
        fixture.Fire();
        var first = fixture.Base.Tick();
        Assert.All(fixture.Contacts(), drone => Assert.Equal(fixture.Base.Npc.Id, drone.TargetId));
        var hits = first.Hits.Concat(Enumerable.Range(0, 10).SelectMany(_ => fixture.Base.Tick().Hits)).ToArray();
        Assert.True(hits.Length >= 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnersSafetyIsCheckedIndependentlyAndChangingItDoesNotReplayAnEarlierIllegalAttack(bool resident)
    {
        using Fixture fixture = new(resident);
        var victim = AddOwner(fixture, 0);
        using (CatalogDbContext catalog = new(fixture.Base.Base.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144','{{\"securityStatus\":0.4}}')"
            );
        }
        using (WorldDbContext world = new(fixture.Base.Base.Db.Path))
        {
            world.Pilots.Find(Member)!.SafetyLevel = ShipSafetyLevel.Disabled;
            world.Pilots.Find(fixture.Base.Pilot)!.SafetyLevel = ShipSafetyLevel.Full;
            world.SaveChanges();
        }
        fixture.Assign(DroneDelegationMode.Assist);
        var weapons = fixture.Base.Base.Db.Services.GetRequiredService<IPilotCombat>();
        void Attack() =>
            weapons.FirePlayerBank(Member, fixture.Gun, new(victim.Ship, SpacePosition.Zero), SpacePosition.Zero, fixture.Base.Now, .01);
        Attack();
        Assert.Empty(fixture.Base.Tick().Hits);
        using (WorldDbContext world = new(fixture.Base.Base.Db.Path))
        {
            Assert.False(world.Set<SavedPilotHostility>().Any(h => h.SourceShipId == fixture.Base.Ship));
            world.Pilots.Find(fixture.Base.Pilot)!.SafetyLevel = ShipSafetyLevel.Partial;
            world.SaveChanges();
        }
        Assert.Empty(fixture.Base.Tick(10).Hits);
        Attack();
        var hits = fixture.Base.Tick().Hits;
        Assert.Equal(5, hits.Count);
        Assert.All(hits, hit => Assert.Equal(victim.Ship, hit.TargetId));
        Assert.Equal(0, DroneCombatTriggerTests.Read(fixture.Base.Base.Db.Path, fixture.Base.Ship).Offensive.Revision);
    }

    [Theory]
    [InlineData(false, 2488)]
    [InlineData(true, 2488)]
    [InlineData(false, 2476)]
    [InlineData(true, 2476)]
    [InlineData(false, 23561)]
    [InlineData(true, 23561)]
    public void CombatKindsKeepTheirMovementAndReturnToTheirDelegateAfterTheEnemyDisappears(bool resident, int type)
    {
        using Fixture fixture = new(resident, type, 1);
        var start = Assert.Single(fixture.Contacts());
        fixture.Assign(DroneDelegationMode.Assist);
        fixture.Fire();
        Assert.Single(fixture.Base.Tick().Hits);
        using (WorldDbContext world = new(fixture.Base.Base.Db.Path))
        {
            world.Set<WorldNpc>().Find(fixture.Base.Npc.Id)!.DestroyedAt = fixture.Base.Now;
            world.SaveChanges();
        }
        Assert.Empty(fixture.Base.Tick(10).Hits);
        var idle = Assert.Single(fixture.Contacts());
        Assert.Equal(DroneActivity.Idle, idle.Activity);
        Assert.Equal(MemberShip, idle.TargetId);
        Assert.NotNull(idle.Delegation);
        if (type == 23561)
        {
            Assert.Equal(start.Position, idle.Position);
            Assert.Equal(SpacePosition.Zero, idle.Velocity);
        }
        else
        {
            Assert.NotEqual(start.Position, idle.Position);
        }
    }
}
