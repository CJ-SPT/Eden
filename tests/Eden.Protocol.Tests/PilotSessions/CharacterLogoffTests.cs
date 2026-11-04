using Eden.Gameplay.Skills;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CharacterLogoffTests
{
    [Fact]
    public void LogoffRemovesShipLocksBindingsAndPendingMessagesWhileTrainingContinues()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO DogmaAttribute(Id,Name) VALUES (76,'maxTargetRange'),(192,'maxLockedTargets'),
                    (552,'signatureRadius'),(564,'scanResolution');
                INSERT INTO TypeAttribute VALUES (601,76,27000),(601,192,3),(601,552,52),(601,564,475);
                """
            );
        }
        db.Store.SetPosition(1000000000001, new(10_000_000, 0, 0));
        db.Store.SetPosition(1000000000002, new(10_001_000, 0, 0));
        using var scope = db.Services.CreateScope();
        var services = scope.ServiceProvider;
        var pilot = db.Store.FindPilot(1000001)!;
        var wing = db.Store.FindPilot(1000002)!;
        var flights = services.GetRequiredService<FlightSessionFactory>();
        using var observer = flights.Create(wing);
        observer.Snapshot();
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], flights.Create, 42, SignedSettings.Empty);
        context.Select(pilot);
        context.Flight.Snapshot();
        var targeting = services.GetRequiredService<TargetingHandler>();
        targeting.Advance(context);
        Assert.IsType<PilotCallResult.Success>(targeting.Call(context, "dogmaIM", "AddTarget", [wing.ShipId]));
        clock.Milliseconds += 60_000;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(wing.ShipId));
        var locks = services.GetRequiredService<SharedTargetLocks>();
        Assert.True(locks.Contains(pilot.ShipId, wing.ShipId));
        var space = services.GetRequiredService<SharedSpace>();
        Assert.Contains(space.Peers(wing), ship => ship.Pilot.ShipId == pilot.ShipId);
        var skills = context.Reference("@skills", PilotBindingLifetime.Pilot);
        var hangar = context.Reference("@hangar", PilotBindingLifetime.Location);
        var account = context.Reference("@account", PilotBindingLifetime.Account);
        var training = services.GetRequiredService<IPilotTraining>();
        var queue = training.Save(pilot.PilotId, [new(3327, 4)], true).Queue;
        context.NextSkillCompletion = queue[0].EndTime;
        context.NotificationsBeforeFlight.Add(new BlueprintsUpdated(pilot.PilotId));
        context.Notifications.Add(new BlueprintsUpdated(pilot.PilotId));
        var handler = services.GetRequiredService<CharacterLogoffHandler>();
        var result = Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "userSvc", "UserLogOffCharacter", Array.Empty<object?>(), null)
        );
        Assert.True(Assert.IsType<bool>(result.Reply.ToBlue()));
        Assert.False(context.Selected);
        Assert.Null(context.ActiveFlight);
        Assert.Null(context.DestroyedShip);
        Assert.Null(context.NextSkillCompletion);
        Assert.Empty(context.NotificationsBeforeFlight);
        Assert.Empty(context.Notifications);
        Assert.False(targeting.IsLocked(wing.ShipId));
        Assert.False(locks.Contains(pilot.ShipId, wing.ShipId));
        Assert.DoesNotContain(space.Peers(wing), ship => ship.Pilot.ShipId == pilot.ShipId);
        Assert.Null(context.ResolveReference(skills.Id));
        Assert.Null(context.ResolveReference(hangar.Id));
        Assert.Equal("@account", context.ResolveReference(account.Id));
        var saved = new SqliteWorld(new(db.Path)).FindPilot(1000001)!;
        Assert.Equal(pilot.Position, saved.Position);
        Assert.Equal(pilot.ShipId, saved.ShipId);
        clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        Assert.Equal(queue, training.Advance(pilot.PilotId).Queue);
        Assert.True(queue[0].PointsAt(clock.Now) > queue[0].StartPoints);
        context.Select(Assert.Single(context.Roster));
        Assert.NotEqual(skills.Id, context.Reference("@skills", PilotBindingLifetime.Pilot).Id);
        Assert.Null(context.ResolveReference(skills.Id));
        Assert.False(targeting.IsLocked(wing.ShipId));
        Assert.False(services.GetRequiredService<PilotUpdateCoordinator>().IsMonitoring);
    }
}
