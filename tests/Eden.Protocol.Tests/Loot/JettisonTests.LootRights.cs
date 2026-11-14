using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Loot;

public sealed partial class JettisonTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAbandonmentUpdatesObserversAndSurvivesExistingContainerCheckpoint(bool liveWorld)
    {
        Clock clock = new(s_start);
        using TestWorld db = NewWorld(liveWorld, clock);
        var pilot = db.Store.FindPilot(AccountId)!;
        long cargo = Seed(db, pilot.ShipId, InventoryFlag.Cargo, 100);
        db.Store.SetPosition(pilot.ShipId, s_position, SystemId);
        var wrecks = db.Services.GetRequiredService<IWrecks>();
        var jettison = db.Services.GetRequiredService<ICargoJettison>();
        long id = jettison.Jettison(pilot.PilotId, pilot.ShipId, [cargo], Now).Contact!.Id;
        if (liveWorld)
        {
            await LiveWorld.Find(db.Path)!.FlushAsync();
        }
        pilot = db.Store.FindPilot(AccountId)!;
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            AccountId,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, clock, new(), [], wrecks: wrecks),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        context.Flight.Updates.Clear();
        var other = db.Store.FindPilot(1000002)!;
        db.Store.SetPosition(other.ShipId, s_position, SystemId);
        using FlightSession observer = new(db.Store, db.Store.FindPilot(1000002)!, clock, new(), [], wrecks: wrecks);
        observer.Snapshot();
        var previous = Assert.Single(observer.VisibleWrecks);
        observer.Updates.Clear();

        object? Abandon(object?[] args) => router.CallWire(context, "beyonce", "CmdAbandonLoot", args, _ => { });
        Assert.IsType<PilotCallResult.Failure>(Abandon([id]));
        Assert.IsType<PilotCallResult.Failure>(Abandon([new List<object?> { id, "invalid" }]));
        Assert.False(Assert.Single(wrecks.List(SystemId, Now)).Abandoned);
        Assert.Null(Abandon([new List<object?> { id }]));
        observer.RefreshWrecks();
        Assert.True(Assert.Single(observer.VisibleWrecks).Abandoned);
        Assert.False(previous.Abandoned);
        var action = Assert.Single(
            observer.WireUpdates().Cast<object?[]>().Select(u => (object?[])u[1]!),
            a => Equals(a[0], "OnSlimItemChange")
        );
        var args = Assert.IsType<object?[]>(action[1]);
        var slim = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(args[1]).State);
        Assert.True(Assert.IsType<bool>(Assert.IsType<object?[]>(slim["lootRights"])[3]));
        Assert.NotEmpty(BlueMarshal.Encode(observer.WireUpdates()));
        if (liveWorld)
        {
            await LiveWorld.Find(db.Path)!.FlushAsync();
        }
        using (WorldDbContext disk = new(WorldConnections.FileConnection(db.Path)))
        {
            Assert.True(disk.Set<SavedJetcan>().Find(id)!.Abandoned);
            Assert.Equal(pilot.PilotId, disk.Set<OwnedItem>().Find(cargo)!.OwnerId);
        }
        if (!liveWorld)
        {
            WorldInitialization.Open(db.Path);
            Assert.True(Assert.Single(wrecks.List(SystemId, Now)).Abandoned);
        }
        Assert.Single(wrecks.Take(other.PilotId, other.ShipId, id, [cargo], null, Now));
        Assert.False(db.Services.GetRequiredService<IPilotCrimewatch>().Read(other.PilotId, SystemId).Timers.Suspect.Running(Now));
        Assert.Empty(wrecks.List(SystemId, Now));
    }

    [Fact]
    public void NativeJetcanTheftPublishesSuspectTimersAndClearsThemAtTheExactDeadline()
    {
        Clock clock = new(s_start);
        using TestWorld db = NewWorld(true, clock);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            string json = System.Text.Json.JsonSerializer.Serialize(new { securityStatus = 0.8 });
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{json})");
        }
        var owner = db.Store.FindPilot(AccountId)!;
        long cargo = Seed(db, owner.ShipId, InventoryFlag.Cargo, 100);
        db.Store.SetPosition(owner.ShipId, s_position, SystemId);
        var wrecks = db.Services.GetRequiredService<IWrecks>();
        long id = db.Services.GetRequiredService<ICargoJettison>().Jettison(owner.PilotId, owner.ShipId, [cargo], Now).Contact!.Id;
        var thief = db.Store.FindPilot(1000002)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<Account>().Find(1000002L)!.CloneGrade = AccountCloneGrade.Omega;
            world.SaveChanges();
        }
        db.Store.SetPosition(thief.ShipId, s_position, SystemId);
        thief = db.Store.FindPilot(1000002)!;
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var crimewatch = scope.ServiceProvider.GetRequiredService<CrimewatchHandler>();
        using PilotCallContext context = new(
            1000002,
            thief.Account,
            [thief],
            p => new(db.Store, p, clock, new(), [], wrecks: wrecks),
            7,
            SignedSettings.Empty
        );
        context.Select(thief);
        context.Flight.Snapshot();
        crimewatch.Enter(context);
        object? Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.CallWire(context, service, method, args, _ => { }, keywords);
        var reference = Assert.IsType<BlueObjectReference>(Call("invbroker", "GetInventoryFromId", [id, false]));
        Assert.Single(Assert.IsType<List<object?>>(Call(context.ResolveReference(reference.Id)!, "List", [])));
        Assert.IsType<PilotCallResult.Failure>(Call("@ship", "MultiAdd", [new List<object?> { cargo }, id]));
        Assert.Null(Call("crimewatch", "SetSafetyLevel", [(long)ShipSafetyLevel.Partial]));
        Assert.Null(Call("@ship", "MultiAdd", [new List<object?> { cargo }, id], new() { ["flag"] = (long)InventoryFlag.Cargo }));
        Assert.Single(context.Notifications, n => n.Name == "OnItemChange");
        crimewatch.Advance(context);
        var timer = Assert.Single(context.Notifications.OfType<CombatTimerChanged>());
        Assert.Equal(CrimewatchTimerState.SuspectCounting, timer.Timer.State);
        Assert.Equal(Now + CrimewatchRules.FlagDuration.Ticks, timer.Timer.ExpiresAt);
        Assert.Contains(thief.PilotId, Assert.Single(context.Notifications.OfType<CrimewatchFlagsChanged>()).Suspects);
        context.Notifications.Clear();
        clock.AdvanceWallTime(CrimewatchRules.FlagDuration - TimeSpan.FromTicks(1));
        crimewatch.Advance(context);
        Assert.Empty(context.Notifications);
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        crimewatch.Advance(context);
        Assert.Equal(CrimewatchTimerState.CriminalIdle, Assert.Single(context.Notifications.OfType<CombatTimerChanged>()).Timer.State);
        Assert.Contains(thief.PilotId, Assert.Single(context.Notifications.OfType<CrimewatchFlagsChanged>()).Idle);
        context.Notifications.Clear();
        crimewatch.Advance(context);
        Assert.Empty(context.Notifications);
    }
}
