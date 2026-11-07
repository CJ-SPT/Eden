using Eden.Data.World.Combat;
using Eden.Data.World.Drones;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class DroneDelegationTests
{
    [Theory]
    [InlineData(false, "foreign")]
    [InlineData(true, "foreign")]
    [InlineData(false, "duplicate")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "fleet")]
    [InlineData(true, "fleet")]
    [InlineData(false, "range")]
    [InlineData(true, "range")]
    [InlineData(false, "offline")]
    [InlineData(true, "offline")]
    [InlineData(false, "skills")]
    [InlineData(true, "skills")]
    [InlineData(false, "dead-owner")]
    [InlineData(true, "dead-owner")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "self")]
    [InlineData(true, "self")]
    [InlineData(false, "capsule")]
    [InlineData(true, "capsule")]
    [InlineData(false, "dead-member")]
    [InlineData(true, "dead-member")]
    public void RejectedAssignmentsPreserveEveryEarlierOrderAndDoNotCreateActivity(bool resident, string failure)
    {
        using Fixture fixture = new(resident);
        long owner = fixture.Base.Pilot;
        long member = Member;
        IReadOnlyList<long> selection = fixture.Ids;
        if (failure == "foreign")
        {
            selection = [fixture.Ids[0], long.MaxValue];
        }
        else if (failure == "duplicate")
        {
            selection = [fixture.Ids[0], fixture.Ids[0]];
        }
        else if (failure == "fleet")
        {
            fixture.Fleets.Leave(Member, fixture.FleetId);
        }
        else if (failure == "range")
        {
            fixture.Base.Base.Db.Store.SetPosition(MemberShip, fixture.Base.Npc.Position + new SpacePosition(70000, 0, 0), SystemId);
            fixture.PublishMember();
        }
        else if (failure == "offline")
        {
            fixture.Space.Leave(MemberShip);
        }
        else if (failure == "skills")
        {
            fixture.Base.Base.Train((int)DroneSkill.Drones, 0);
        }
        else if (failure == "missing")
        {
            member = long.MaxValue;
        }
        else if (failure == "self")
        {
            member = owner;
        }
        else if (failure == "capsule")
        {
            using (Eden.World.Catalog.CatalogDbContext catalog = new(fixture.Base.Base.CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (29,'Capsule',6);
                    INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Capacity,Mass,Radius) VALUES (670,'Capsule',29,0,32000,2);
                    INSERT OR IGNORE INTO TypeAttribute SELECT 670,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=601;
                    INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','670','{{"published":1,"volume":1000}}');
                    """
                );
            }
            using WorldDbContext world = new(fixture.Base.Base.Db.Path);
            world.Ships.Find(MemberShip)!.TypeId = 670;
            world.SaveChanges();
            fixture.PublishMember();
        }
        else if (failure is "dead-owner" or "dead-member")
        {
            long victimPilot = failure == "dead-owner" ? owner : Member;
            long victimShip = failure == "dead-owner" ? fixture.Base.Ship : MemberShip;
            var defense = fixture.Base.Base.Db.Services.GetRequiredService<IPilotHealth>().Read(victimPilot, fixture.Base.Now).Defense;
            using WorldDbContext world = new(fixture.Base.Base.Db.Path);
            var health = world.Set<ShipHealthState>().Find(victimShip) ?? world.Add(new ShipHealthState { ShipId = victimShip }).Entity;
            health.ShieldCharge = 0;
            health.ArmorDamage = defense.Armor;
            health.StructureDamage = defense.Structure;
            health.UpdatedAt = fixture.Base.Now;
            world.SaveChanges();
        }
        var before = fixture.Contacts();
        Assert.Throws<InvalidDataException>(() =>
            fixture.Base.Drones.Delegate(owner, selection, DroneDelegationMode.Assist, member, fixture.Base.Now)
        );
        Assert.Equal(before, fixture.Contacts());
        Assert.All(fixture.Contacts(), drone => Assert.Null(drone.Delegation));
    }

    [Fact]
    public void Version101MigrationPreservesPhysicalDamageAndDeadlineWithoutInventingADelegation()
    {
        using Fixture fixture = new(false);
        long deadline = fixture.Base.Now + TimeSpan.TicksPerMinute;
        var positions = fixture.Contacts().Select(d => d.Position).ToArray();
        using (WorldDbContext world = new(fixture.Base.Base.Db.Path))
        {
            world.Set<WorldDrone>().ExecuteUpdate(set => set.SetProperty(d => d.ArmorDamage, 5).SetProperty(d => d.ReadyAt, deadline));
            world.Database.ExecuteSqlRaw(
                """
                DROP INDEX IX_WorldDrone_DelegateShipId;
                ALTER TABLE WorldDrone DROP COLUMN DelegationMode;
                ALTER TABLE WorldDrone DROP COLUMN DelegatePilotId;
                ALTER TABLE WorldDrone DROP COLUMN DelegateShipId;
                ALTER TABLE WorldDrone DROP COLUMN DelegateFleetId;
                ALTER TABLE WorldDrone DROP COLUMN OwnerMembership;
                ALTER TABLE WorldDrone DROP COLUMN DelegateMembership;
                ALTER TABLE WorldDrone DROP COLUMN ReactionRevision;
                PRAGMA user_version=101;
                """
            );
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Base.Base.Clock);
        using WorldDbContext saved = new(fixture.Base.Base.Db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.All(
            saved.Set<WorldDrone>(),
            drone =>
            {
                Assert.Equal(DroneDelegationMode.None, drone.DelegationMode);
                Assert.Equal(0, drone.ControllerShipId);
                Assert.Equal(5, drone.ArmorDamage);
                Assert.Equal(deadline, drone.ReadyAt);
            }
        );
        Assert.Equal(positions, fixture.Contacts().Select(d => d.Position));
    }
}
