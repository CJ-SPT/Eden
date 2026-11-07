using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Combat;

public sealed class LiveDroneRulesTests
{
    [Fact]
    public void LateMovementIsCappedDuplicateCallbacksDoNothingAndLostTargetsReturnToIdle()
    {
        DamageVector resonance = new(1, 1, 1, 1);
        HullDefense defense = new(100, 100, 100, 100000, resonance, resonance, resonance);
        DroneProfile profile = new(2486, 5, 5, 1000000, 10, 1, 3000, 500, 1000, 1000, 2000, 1, 40, 4000, new(0, 0, 10, 0), defense);
        DroneContact contact = new(
            10,
            1,
            2,
            1000044,
            30000144,
            profile,
            DroneActivity.Fighting,
            20,
            new(10000, 0, 0),
            SpacePosition.Zero,
            new(10, defense, defense.Undamaged)
        );
        LiveDrone drone = new(contact, TimeSpan.TicksPerSecond, 0, 0, 0, 0);
        LiveCombatShip ship = new(
            1,
            2,
            1000044,
            33151,
            30000144,
            SpacePosition.Zero,
            100,
            50000,
            100,
            100000,
            100,
            0,
            new(defense, defense.Undamaged, 0),
            [],
            new Dictionary<long, LiveTurret>(),
            new Dictionary<long, IReadOnlyList<long>>(),
            419,
            100,
            new Dictionary<long, Eden.Gameplay.Characters.CombatParty>()
        );
        var oneSecond = LiveDroneRules.Advance(drone, ship, null, 2 * TimeSpan.TicksPerSecond, 2, 0.5);
        var late = LiveDroneRules.Advance(drone, ship, null, 60 * TimeSpan.TicksPerSecond, 2, 0.5);
        Assert.Equal(oneSecond.Drone.Contact.Position, late.Drone.Contact.Position);
        Assert.Equal(DroneActivity.Idle, late.Drone.Contact.Activity);
        Assert.Equal(ship.ShipId, late.Drone.Contact.TargetId);
        Assert.Null(late.Damage);
        var duplicate = LiveDroneRules.Advance(late.Drone, ship, null, 60 * TimeSpan.TicksPerSecond, 2, 0.5);
        Assert.Same(late.Drone, duplicate.Drone);
        Assert.Null(duplicate.Damage);
        var returning = drone with { Contact = contact with { Activity = DroneActivity.ReturningToBay, Position = SpacePosition.Zero } };
        Assert.True(LiveDroneRules.Advance(returning, ship, null, 2 * TimeSpan.TicksPerSecond, 2, 0.5).Recall);
        DroneTarget pilot = new(contact.TargetId, new(11000, 0, 0), SpacePosition.Zero, 50, 40);
        var shot = LiveDroneRules.AdvanceTarget(drone, ship, pilot, 2 * TimeSpan.TicksPerSecond, 2, 0.5);
        Assert.True(shot.Damage!.Total > 0);
        Assert.Equal(pilot.Id, shot.Drone.Contact.ShotTargetId);
        Assert.Equal(6 * TimeSpan.TicksPerSecond, shot.Drone.ReadyAt);
        Assert.Null(LiveDroneRules.AdvanceTarget(shot.Drone, ship, pilot, 3 * TimeSpan.TicksPerSecond, 3, 0.5).Damage);
        var fast = pilot with { Velocity = new(0, 1000000, 0) };
        Assert.Equal(0, LiveDroneRules.AdvanceTarget(drone, ship, fast, 2 * TimeSpan.TicksPerSecond, 2, 0.5).Damage!.Total);
        var distant = pilot with { Position = new(100000, 0, 0) };
        Assert.Null(LiveDroneRules.AdvanceTarget(drone, ship, distant, 2 * TimeSpan.TicksPerSecond, 2, 0.5).Damage);
    }
}
