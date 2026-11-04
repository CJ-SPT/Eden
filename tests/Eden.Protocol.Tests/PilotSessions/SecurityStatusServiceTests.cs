using Eden.Gameplay.Characters;
using Eden.Protocol.Characters;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class SecurityStatusServiceTests
{
    [Fact]
    public void NativeStatusHistoryAndOwnerNotificationUseCommittedSecurityWithPrivateHistory()
    {
        var clock = CalendarTestData.Clock();
        using PilotCombatTests.Fixture f = new(clock);
        SecurityTestData.CatalogReward(f.Db);
        using CourierServiceTests.Client owner = new(f.Db, clock, 1000001);
        using CourierServiceTests.Client other = new(f.Db, clock, 1000002);
        Assert.Same(PilotCallResult.Unsupported, owner.Call("GetMySecurityStatus", [], "crimewatch"));
        owner.Refresh();
        other.Refresh();
        Assert.Equal(0d, Reply(owner.Call("GetMySecurityStatus", [], "crimewatch")));
        f.AlmostDefeated();
        Assert.True(f.Fire().Destroyed);
        owner.Advance();
        owner.Context.Notifications.Clear();
        Assert.Equal(0d, Reply(owner.Call("GetMySecurityStatus", [], "crimewatch")));
        Assert.Empty(Assert.IsType<List<object?>>(Reply(owner.Call("GetSecurityStatusTransactions", [], "crimewatch"))));
        clock.AdvanceWallTime(SecurityStatusRules.RewardWindow);
        Assert.Equal(0.0005, Assert.IsType<double>(Reply(owner.Call("GetMySecurityStatus", [], "crimewatch"))), 10);
        var update = Assert.Single(owner.Context.Notifications.OfType<SecurityStatusUpdated>());
        Assert.Equal(0.0005, Assert.IsType<double>(Assert.Single(update.Arguments)), 10);
        var transactions = Assert.IsType<List<object?>>(Reply(owner.Call("GetSecurityStatusTransactions", [], "crimewatch")));
        var row = Fields(Assert.Single(transactions));
        Assert.Equal(378L, row["eventTypeID"]);
        Assert.Equal(1000127L, row["otherOwnerID"]);
        Assert.Equal((long)f.Npc.SystemId, row["locationID"]);
        Assert.Equal(clock.Now, row["eventDate"]);
        Assert.Equal(0.00005, Assert.IsType<double>(row["modification"]), 10);
        Assert.Equal(0.0005, Assert.IsType<double>(row["newValue"]), 10);
        Assert.True(Assert.IsType<long>(row["eventID"]) > 0);
        Assert.Single(owner.Context.Notifications.OfType<SecurityStatusUpdated>());
        Assert.Equal(
            0.0005,
            Assert.IsType<double>(Reply(other.Call("GetCharacterSecurityStatus", [SecurityTestData.Pilot], "crimewatch"))),
            10
        );
        Assert.Empty(Assert.IsType<List<object?>>(Reply(other.Call("GetSecurityStatusTransactions", [], "crimewatch"))));
        Assert.Empty(other.Context.Notifications.OfType<SecurityStatusUpdated>());
        Assert.IsType<PilotCallResult.Failure>(other.Call("GetSecurityStatusTransactions", [SecurityTestData.Pilot], "crimewatch"));
    }

    [Fact]
    public void InvalidSecurityShapesAndForeignHistoryArgumentsFailWithoutMutation()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        foreach (
            var (method, args) in new (string, object?[])[]
            {
                ("GetMySecurityStatus", [90000002L]),
                ("GetSecurityStatusTransactions", [90000002L]),
                ("GetCharacterSecurityStatus", []),
                ("GetCharacterSecurityStatus", [0L]),
                ("GetCharacterSecurityStatus", [long.MaxValue]),
                ("GetCharacterSecurityStatus", ["pilot"]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(client.Call(method, args, "crimewatch"));
        }
        Assert.IsType<PilotCallResult.Failure>(client.Call("GetMySecurityStatus", [], "crimewatch", new() { ["value"] = 5d }));
        Assert.Equal(0d, Reply(client.Call("GetMySecurityStatus", [], "crimewatch")));
    }

    [Fact]
    public void ACommittedKillBetweenTheReadAndReturnCannotBeLostAndTheNotificationWaitsInThePreFlightQueue()
    {
        var clock = CalendarTestData.Clock();
        using PilotCombatTests.Fixture f = new(clock);
        SecurityTestData.CatalogReward(f.Db);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new FlightSession(f.Db.Store, p, clock, new(), []),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var security = f.Db.Services.GetRequiredService<IPilotSecurityStatus>();
        ReadBoundary wrapper = new(
            security,
            () =>
            {
                f.AlmostDefeated();
                Assert.True(f.Fire().Destroyed);
            }
        );
        SecurityStatusHandler handler = new(wrapper, clock);
        handler.Enter(context);
        handler.Advance(context);
        Assert.True(handler.IsMonitoring);
        clock.AdvanceWallTime(SecurityStatusRules.RewardWindow);
        handler.Advance(context);
        handler.Advance(context);
        Assert.False(context.Flight.InBallpark);
        Assert.Equal(0.0005, Assert.Single(context.Notifications.OfType<SecurityStatusUpdated>()).Value, 10);
        Assert.Single(security.History(SecurityTestData.Pilot));
    }

    private sealed class ReadBoundary(IPilotSecurityStatus inner, Action afterRead) : IPilotSecurityStatus
    {
        private Action? _afterRead = afterRead;
        public long Revision => inner.Revision;

        public SecurityStatusView Read(long pilotId)
        {
            var view = inner.Read(pilotId);
            var action = _afterRead;
            _afterRead = null;
            action?.Invoke();
            return view;
        }

        public IReadOnlyList<SecurityStatusTransaction> History(long pilotId) => inner.History(pilotId);
    }

    private static object? Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return BlueMarshal.Decode(BlueMarshal.Encode(((PilotCallResult.Success)result).Reply));
    }

    private static Dictionary<string, object?> Fields(object? value) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(value).State);
}
