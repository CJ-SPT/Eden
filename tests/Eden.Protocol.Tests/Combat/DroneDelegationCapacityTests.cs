using Eden.Gameplay.Drones;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class DroneDelegationTests
{
    private sealed record AdditionalOwner(long Pilot, long Ship, long[] Drones);

    private static AdditionalOwner AddOwner(Fixture fixture, int index)
    {
        long account = 1100000 + index;
        long pilot = 91000000 + index;
        long ship = 7000000000000 + index;
        long stack = 7100000000000 + index * 100;
        using (WorldDbContext world = new(fixture.Base.Base.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
            world.Database.ExecuteSql($"INSERT INTO WorldAccount(Id,UserName) VALUES ({account},{"Delegation " + index})");
            world.Database.ExecuteSql(
                $"INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) VALUES({ship},{pilot},16229,60003760,{"Delegation " + index})"
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId)
                VALUES({pilot},{account},{"Delegation " + index},1000044,{ship});
                """
            );
            world.Database.ExecuteSql(
                $"INSERT INTO WorldPilotSkill SELECT {pilot},TypeId,Points FROM WorldPilotSkill WHERE PilotId={fixture.Base.Pilot}"
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity,Singleton,Online)
                VALUES({stack},{pilot},2488,{ship},{(int)Eden.Gameplay.Inventory.InventoryFlag.DroneBay},5,0,0);
                """
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPosition(ShipId,SystemId,X,Y,Z)
                VALUES({ship},{SystemId},{fixture.Base.Npc.Position.X + 1000 + index * 100},
                    {fixture.Base.Npc.Position.Y},{fixture.Base.Npc.Position.Z});
                """
            );
            transaction.Commit();
        }
        var identity = fixture.Base.Base.Db.Store.FindPilot(account)!;
        fixture.Fleets.Update(Guid.NewGuid(), new(pilot, identity.CorpId, ship, identity.ShipTypeId, SystemId, identity.Position));
        fixture.Join(pilot);
        fixture.Space.Publish(new(PilotContact.From(identity), SpacePosition.Zero, null, 0, null));
        var drones = fixture.Base.Drones.Launch(pilot, [new(stack, 5)], fixture.Base.Now).Launches.Single().DroneIds.ToArray();
        return new(pilot, ship, drones);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedAssignmentsShareTwentySlotsAndReassignmentAndStaleReservationsCannotConsumeExtraSlots(bool resident)
    {
        using Fixture fixture = new(resident);
        var owners = Enumerable.Range(0, 4).Select(index => AddOwner(fixture, index)).ToArray();
        fixture.Assign(DroneDelegationMode.Assist);
        for (int index = 0; index < 3; index++)
        {
            fixture.Base.Drones.Delegate(
                owners[index].Pilot,
                owners[index].Drones,
                index % 2 == 0 ? DroneDelegationMode.Guard : DroneDelegationMode.Assist,
                Member,
                fixture.Base.Now
            );
        }
        var before = fixture.Contacts();
        Assert.Throws<InvalidDataException>(() =>
            fixture.Base.Drones.Delegate(owners[3].Pilot, owners[3].Drones, DroneDelegationMode.Guard, Member, fixture.Base.Now)
        );
        Assert.Equal(before, fixture.Contacts());
        Assert.Equal(20, before.Count(d => d.Delegation is not null));
        fixture.Assign(DroneDelegationMode.Guard);
        Assert.Equal(20, fixture.Contacts().Count(d => d.Delegation is not null));
        fixture.Fleets.Leave(owners[0].Pilot, fixture.FleetId);
        fixture.Base.Drones.Delegate(owners[3].Pilot, owners[3].Drones, DroneDelegationMode.Guard, Member, fixture.Base.Now);
        var after = fixture.Contacts();
        Assert.Equal(20, after.Count(d => d.Delegation is not null));
        Assert.All(after.Where(d => d.PilotId == owners[0].Pilot), d => Assert.Null(d.Delegation));
        Assert.All(after.Where(d => d.PilotId == owners[3].Pilot), d => Assert.Equal(DroneDelegationMode.Guard, d.Delegation!.Mode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingAssignmentsCannotOverbookTheRecipientsRemainingSlots(bool resident)
    {
        using Fixture fixture = new(resident);
        var owners = Enumerable.Range(0, 4).Select(index => AddOwner(fixture, index)).ToArray();
        fixture.Assign(DroneDelegationMode.Assist);
        for (int index = 0; index < 2; index++)
        {
            fixture.Base.Drones.Delegate(owners[index].Pilot, owners[index].Drones, DroneDelegationMode.Guard, Member, fixture.Base.Now);
        }
        var outcomes = await Task.WhenAll(
            owners
                .Skip(2)
                .Select(owner =>
                    Task.Run(() =>
                    {
                        try
                        {
                            fixture.Base.Drones.Delegate(owner.Pilot, owner.Drones, DroneDelegationMode.Assist, Member, fixture.Base.Now);
                            return true;
                        }
                        catch (InvalidDataException)
                        {
                            return false;
                        }
                    })
                )
        );
        Assert.Single(outcomes, accepted => accepted);
        Assert.Equal(20, fixture.Contacts().Count(d => d.Delegation is not null));
        Assert.Equal(25, fixture.Contacts().Length);
        Assert.Equal(5, fixture.Contacts().Count(d => d.Delegation is null));
    }
}
