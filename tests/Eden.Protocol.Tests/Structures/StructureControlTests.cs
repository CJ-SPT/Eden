using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Structures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureControlTests
{
    [Fact]
    public async Task DockedControlInstallsAthanorCoreFuelAndServiceAndCheckpointsTheRealShip()
    {
        using Fixture f = new(liveWorld: true);
        var structure = f.Deploy(35835);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        structure = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.Equal(StructureState.Onlining, structure.State);
        Assert.Null(structure.StateEndsAt);
        long core = f.Item(structure.Profile.CoreTypeId!.Value);
        long fuel = f.Item(4051, 1000);
        long module = f.Item(35899);
        var docked = f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        using var scope = f.Db.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext context = new(1000001, docked.Account, [docked], factory.Create, 51, SignedSettings.Empty);
        context.Select(docked);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var initial = Changes(PilotSessionWire.SessionChange(1000001, 51, docked));
        Assert.Equal(new object?[] { null, (long)Fixture.SystemId }, initial["solarsystemid"]);
        Assert.Equal(new object?[] { null, (long)Fixture.SystemId }, initial["locationid"]);
        Assert.Equal(new object?[] { null, structure.Id }, initial["structureid"]);
        var dockedInfo = Assert.IsType<DogmaAllInfo>(Call("dogmaIM", "GetAllInfo", [true, true, true]).Reply);
        Assert.Contains(structure.Id, dockedInfo.StructureInfo!.Keys);
        Assert.Contains(docked.ShipId, dockedInfo.ShipInfo.Keys);
        Call("structureControl", "TakeControl", [structure.Id]);
        Assert.Equal(structure.Id, context.ControlledStructureId);
        Assert.Equal(docked.ShipId, context.Pilot.ShipId);
        Assert.Equal(f.PilotId, Call("structureControl", "GetStructurePilot", [structure.Id]).Reply.ToBlue());
        var delta = Changes(PilotSessionWire.SessionChange(1000001, 51, docked, docked, structure.Id));
        Assert.Single(delta);
        Assert.Equal(new object?[] { docked.ShipId, structure.Id }, delta["shipid"]);
        var snapshot = Assert.Single(context.Flight.Updates.Select(update => update.Command).OfType<DestinyCommand.SetState>());
        Assert.Equal(structure.Id, snapshot.Snapshot.Ego);
        Assert.Contains(snapshot.Snapshot.Slims.OfType<StaticSlimItem>(), slim => slim.Body.Id == structure.Id);
        var controlInfo = Assert.IsType<DogmaAllInfo>(Call("dogmaIM", "GetAllInfo", [true, true, true]).Reply);
        Assert.Contains(structure.Id, controlInfo.ShipInfo.Keys);
        Assert.Equal(structure.Id, controlInfo.ActiveShipId);
        Assert.Equal(structure.Id, controlInfo.StructureInfo![structure.Id].Item!.Id);
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "structureDocking", "Undock", new object?[] { structure.Id, docked.ShipId }, _ => { })
        );
        var reference = Assert.IsType<BlueObjectReference>(Call("invbroker", "GetInventoryFromId", [structure.Id]).Reply.ToBlue());
        string inventory = context.ResolveReference(reference.Id)!;
        Assert.StartsWith("@structure-inventory:", inventory);
        Assert.Equal(structure.Id, Assert.IsType<InventoryRow>(Call(inventory, "GetSelfInvItem", []).Reply).Id);
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(
                context,
                inventory,
                "MultiAdd",
                new object?[]
                {
                    new List<object?> { fuel },
                    docked.ShipId,
                },
                _ => { },
                new() { ["flag"] = 180L }
            )
        );
        Assert.False(f.Structures.Read(f.PilotId, structure.Id)!.HasCore);
        Install(core, StructureRules.CoreRoomFlag);
        var cored = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.True(cored.HasCore);
        Assert.Equal(f.Clock.Now + StructureRules.RepairDuration, cored.StateEndsAt);
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(
                context,
                inventory,
                "MultiAdd",
                new object?[]
                {
                    new List<object?> { core },
                    docked.ShipId,
                },
                _ => { },
                new() { ["flag"] = 180L }
            )
        );
        Install(fuel, StructureRules.FuelBayFlag);
        Assert.Equal(
            1000,
            f.Structures.Inventory(f.PilotId, structure.Id).Single(item => item.FlagId == StructureRules.FuelBayFlag).Quantity
        );
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(15));
        Install(module, StructureRules.FirstServiceSlot);
        Call("dogmaIM", "SetModuleOnline", [structure.Id, module]);
        var ready = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.Equal(StructureState.ShieldVulnerable, ready.State);
        Assert.True(Assert.Single(ready.Modules).Online);
        Assert.Contains(StructureService.Reprocessing, ready.Services);
        Call("structureControl", "ReleaseControl", []);
        Assert.Null(context.ControlledStructureId);
        Assert.Null(Call("structureControl", "GetStructurePilot", [structure.Id]).Reply.ToBlue());
        Assert.Equal(
            new object?[] { structure.Id, docked.ShipId },
            Changes(PilotSessionWire.SessionChange(1000001, 51, docked, docked, null, structure.Id))["shipid"]
        );
        await Eden.World.Runtime.LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "controlled-structure.sqlite");
        File.Copy(f.Db.Path, copy);
        using WorldDbContext reopened = new(copy);
        Assert.Equal(docked.ShipId, reopened.Pilots.Find(f.PilotId)!.ActiveShipId);
        Assert.Equal(structure.Id, reopened.Ships.Find(docked.ShipId)!.StructureId);
        Assert.True(reopened.Set<SavedStructure>().Find(structure.Id)!.HasCore);
        Assert.Single(reopened.Set<OwnedItem>(), item => item.LocationId == structure.Id && item.FlagId == StructureRules.CoreRoomFlag);

        PilotCallResult.Success Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            Assert.IsType<PilotCallResult.Success>(router.Call(context, service, method, args, _ => { }, keywords));

        void Install(long item, int flag) =>
            Call(inventory, "MultiAdd", [new List<object?> { item }, docked.ShipId], new() { ["flag"] = (long)flag });
    }

    [Fact]
    public async Task ControlRequiresAuthorityAndExclusiveSessionAndReleasesOnLogoffAndDisconnect()
    {
        using Fixture f = new(liveWorld: true);
        // The last NPC station can be in another system after deploying and docking at an Upwell structure.
        using (WorldDbContext saved = new(f.Db.Path))
        {
            saved.Ships.Find(f.ShipId)!.StationId = 60003760;
            saved.SaveChanges();
        }
        var structure = f.Online(35835);
        Assert.True(f.Structures.CanControl(f.PilotId, structure.Id));
        Assert.False(f.Structures.CanControl(90000002, structure.Id));
        Assert.Throws<InvalidDataException>(() => f.Structures.AuthorizeControl(f.PilotId, structure.Id));
        var docked = f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        using var scope = f.Db.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext first = new(1000001, docked.Account, [docked], factory.Create, 61, SignedSettings.Empty);
        using PilotCallContext second = new(1000001, docked.Account, [docked], factory.Create, 62, SignedSettings.Empty);
        first.Select(docked);
        second.Select(docked);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.IsType<PilotCallResult.Success>(Take(first));
        Assert.IsType<PilotCallResult.Failure>(Take(second));
        Assert.Equal(structure.Id, first.ControlledStructureId);
        Assert.Null(second.ControlledStructureId);
        first.LogOff();
        Assert.IsType<PilotCallResult.Success>(Take(second));
        second.Dispose();
        Assert.Null(scope.ServiceProvider.GetRequiredService<SharedStructureControl>().Pilot(structure.Id));
        Assert.Equal(docked.ShipId, f.Db.Store.FindPilot(1000001)!.ShipId);

        Queue<object?> requests = new(
            new[]
            {
                PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [f.PilotId]),
                PilotServicesTests.Request(
                    "invbroker",
                    "MachoBindObject",
                    [
                        new object?[] { (long)Fixture.SystemId, 5L },
                        new object?[] { "GetInventoryFromId", new object?[] { structure.Id }, new Dictionary<string, object?>() },
                    ]
                ),
                PilotServicesTests.Request(
                    "dogmaIM",
                    "MachoBindObject",
                    [
                        new object?[] { (long)Fixture.SystemId, 5L },
                        new object?[] { "GetAllInfo", new object?[] { true, true, true }, new Dictionary<string, object?>() },
                    ]
                ),
                PilotServicesTests.Request("structureControl", "TakeControl", [structure.Id]),
                PilotServicesTests.Request(
                    "beyonce",
                    "MachoBindObject",
                    [
                        (long)Fixture.SystemId,
                        new object?[] { "UpdateStateRequest", Array.Empty<object?>(), new Dictionary<string, object?>() },
                    ]
                ),
                PilotServicesTests.Request("dogmaIM", "GetAllInfo", [true, true, true]),
                PilotServicesTests.Request("structureControl", "ReleaseControl", []),
            }
        );
        List<BlueInstance> packets = [];
        using var nativeScope = f.Db.Services.CreateScope();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            nativeScope
                .ServiceProvider.GetRequiredService<IPilotSession>()
                .RunAsync(
                    () => requests.TryDequeue(out var request) ? Task.FromResult(request) : throw new EndOfStreamException(),
                    packet =>
                    {
                        packets.Add(Assert.IsType<BlueInstance>(packet));
                        return Task.CompletedTask;
                    },
                    _ => { },
                    1000001,
                    63,
                    SignedSettings.Empty,
                    CancellationToken.None
                )
        );
        var errors = packets
            .Where(packet => packet.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal))
            .Select(packet => BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 3)[2])
            .Select(error =>
                error is string message ? message : System.Text.Encoding.UTF8.GetString(BlueRead.Bytes(BlueRead.Tuple(error, 1)[0]))
            )
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));
        var shipChanges = packets
            .Where(packet => packet.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal))
            .Select(Changes)
            .Where(change => change.ContainsKey("shipid"))
            .ToArray();
        Assert.Contains(
            shipChanges,
            change => BlueRead.Tuple(change["shipid"], 2).SequenceEqual(new object?[] { docked.ShipId, structure.Id })
        );
        Assert.Contains(
            shipChanges,
            change => BlueRead.Tuple(change["shipid"], 2).SequenceEqual(new object?[] { structure.Id, docked.ShipId })
        );
        Assert.Null(nativeScope.ServiceProvider.GetRequiredService<SharedStructureControl>().Pilot(structure.Id));

        PilotCallResult Take(PilotCallContext context) =>
            router.Call(context, "structureControl", "TakeControl", new object?[] { structure.Id }, _ => { });
    }

    private static Dictionary<string, object?> Changes(BlueInstance packet)
    {
        var decoded = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(packet)));
        return BlueRead.Fields(BlueRead.Tuple(BlueRead.Tuple(BlueRead.Tuple(decoded.State, 14)[4], 3)[1], 2)[1]);
    }
}
