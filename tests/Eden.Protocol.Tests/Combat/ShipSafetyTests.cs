using Eden.Gameplay.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class ShipSafetyTests
{
    private static PilotCallContext Client(TestWorld db)
    {
        var pilot = db.Store.FindPilot(1000001)!;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new SharedSpace(), []),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        return context;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NativeSafetySwitchReturnsNoneAndPersistsOnlyTheSelectedPilotsChoice(long level)
    {
        using TestWorld db = new();
        using var context = Client(db);
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var store = db.Services.GetRequiredService<IPilotSafety>();
        Assert.Equal(ShipSafetyLevel.Full, store.Read(context.Pilot.PilotId));
        Assert.Null(router.CallWire(context, "crimewatch", "SetSafetyLevel", new object?[] { level }, _ => { }));
        Assert.Equal((ShipSafetyLevel)level, store.Read(context.Pilot.PilotId));
        Assert.Equal(ShipSafetyLevel.Full, store.Read(db.Store.FindPilot(1000002)!.PilotId));
        var state = Assert.IsType<object?[]>(router.CallWire(context, "crimewatch", "GetClientStates", Array.Empty<object?>(), _ => { }));
        Assert.Equal(4, state.Length);
        Assert.Equal((int)level, state[3]);
        Assert.Empty(context.Notifications);
        WorldInitialization.Initialize(new(db.Path));
        using var reconnected = Client(db);
        Assert.Equal((ShipSafetyLevel)level, db.Services.GetRequiredService<IPilotSafety>().Read(reconnected.Pilot.PilotId));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(3L)]
    [InlineData("1")]
    [InlineData(null)]
    public void InvalidSafetyValuesPreserveTheSavedChoice(object? level)
    {
        using TestWorld db = new();
        using var context = Client(db);
        var store = db.Services.GetRequiredService<IPilotSafety>();
        store.Set(context.Pilot.PilotId, ShipSafetyLevel.Partial);
        var handler = db.Services.GetRequiredService<DockedCompatibilityHandler>();
        Assert.IsType<PilotCallResult.Failure>(handler.Call(context, "crimewatch", "SetSafetyLevel", [level]));
        Assert.Equal(ShipSafetyLevel.Partial, store.Read(context.Pilot.PilotId));
        Assert.Throws<InvalidDataException>(() => store.Set(context.Pilot.PilotId, (ShipSafetyLevel)99));
        Assert.Equal(ShipSafetyLevel.Partial, store.Read(context.Pilot.PilotId));
    }

    [Fact]
    public void VersionThirtyFiveMigrationDefaultsToFullAndRetainsPilotState()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(TestWorld.DropSafetySchema + "PRAGMA user_version=35");
        }
        WorldInitialization.Initialize(new(db.Path));
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
        Assert.Equal(ShipSafetyLevel.Full, db.Services.GetRequiredService<IPilotSafety>().Read(pilot.PilotId));
    }
}
