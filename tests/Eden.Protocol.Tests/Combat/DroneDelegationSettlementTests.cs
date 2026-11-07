using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
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
    public void AssistedDronesCannotRecursivelyTriggerAnotherAssistedFlight(bool resident)
    {
        using Fixture fixture = new(resident);
        var relay = AddOwner(fixture, 0);
        fixture.Base.Drones.Delegate(fixture.Base.Pilot, fixture.Ids, DroneDelegationMode.Assist, relay.Pilot, fixture.Base.Now);
        fixture.Base.Drones.Delegate(relay.Pilot, relay.Drones, DroneDelegationMode.Assist, Member, fixture.Base.Now);
        fixture.Fire();
        Assert.Empty(fixture.Base.Tick().Hits);
        Assert.Equal(5, fixture.Base.Drones.Advance(relay.Pilot, fixture.Base.Now, 1).Hits.Count);
        Assert.Empty(fixture.Base.Tick(10).Hits);
        Assert.All(fixture.Contacts().Where(d => d.PilotId == fixture.Base.Pilot), d => Assert.Equal(DroneActivity.Idle, d.Activity));
        Assert.Equal(0, DroneCombatTriggerTests.Read(fixture.Base.Base.Db.Path, relay.Ship).Offensive.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FatalAssistedShotSettlesOnceAndReturnsSurvivingDronesToTheirDelegate(bool resident)
    {
        using Fixture fixture = new(resident);
        fixture.Fire();
        using (CatalogDbContext catalog = new(fixture.Base.Base.CatalogPath, writable: true))
        {
            // Prevent passive recharge from extending this single-shot destruction probe.
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1e20 WHERE ItemTypeId=30380 AND AttributeId=479");
        }
        fixture.Assign(DroneDelegationMode.Assist);
        fixture.Base.Base.Clock.Seconds += 10;
        fixture.Fire();
        using (WorldDbContext world = new(fixture.Base.Base.Db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(fixture.Base.Npc.Id)!;
            npc.ShieldCharge = 0;
            npc.ArmorDamage = fixture.Base.Npc.Health.Defense.Armor;
            npc.StructureDamage = fixture.Base.Npc.Health.Defense.Structure - 1;
            npc.UpdatedAt = fixture.Base.Now;
            world.SaveChanges();
        }
        var hits = fixture.Base.Tick().Hits;
        Assert.Single(hits, hit => hit.Health.Remaining.Destroyed);
        Assert.Empty(fixture.Base.Tick(10).Hits);
        using WorldDbContext saved = new(fixture.Base.Base.Db.Path);
        Assert.Single(saved.Set<NpcBounty>());
        Assert.All(
            fixture.Contacts(),
            d =>
            {
                Assert.NotNull(d.Delegation);
                Assert.Equal(MemberShip, d.TargetId);
                Assert.Equal(DroneActivity.Idle, d.Activity);
            }
        );
    }

    [Fact]
    public void WarmGuardReactionsUseResidentIncomingTriggersWithoutSql()
    {
        using Fixture fixture = new(true);
        fixture.Assign(DroneDelegationMode.Guard);
        fixture.Fire();
        var combat = fixture.Base.Base.Db.Services.GetRequiredService<INpcCombat>();
        void Cycle()
        {
            fixture.Base.Base.Clock.Seconds += 10;
            fixture.Database.MotionStamps[SystemId] = 1;
            Assert.NotNull(combat.Fire(Member, MemberShip, fixture.Base.Npc.Id, SpacePosition.Zero, fixture.Base.Now, .01, 1));
            Assert.Equal(5, fixture.Base.Tick().Hits.Count);
        }
        Cycle();
        Cycle();
        using var metrics = SessionPerformance.Begin("warm guarding drones", _ => { }, force: true);
        for (int frame = 0; frame < 12; frame++)
        {
            Cycle();
            Assert.All(fixture.Contacts(), drone => Assert.Equal(DroneDelegationMode.Guard, drone.Delegation!.Mode));
        }
        Assert.Equal(0, metrics!.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
