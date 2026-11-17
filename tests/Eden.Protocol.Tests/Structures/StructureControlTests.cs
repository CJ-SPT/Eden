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
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureControlTests
{
    [Fact]
    public async Task DockedControlInstallsAthanorCoreFuelAndServiceAndCheckpointsTheRealShip()
    {
        using Fixture f = new(liveWorld: true);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            const string moon = """
                {"security":0.4,"planets":{"40000990":{"moons":{
                  "40000991":{"miningBeacon":{"position":{"x":10000000000,"y":0,"z":0}}}
                }}}}
                """;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30001392',{moon})");
        }
        var structure = f.Structures.Deploy(f.PilotId, new(f.Item(35835), Fixture.Point, Math.PI / 2, "Rotated refinery")).Structure;
        Assert.Equal(40000991, structure.MoonId);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        structure = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.Equal(StructureState.Onlining, structure.State);
        Assert.Null(structure.StateEndsAt);
        long core = f.Item(structure.Profile.CoreTypeId!.Value);
        long fuel = f.Item(4051, 6000);
        long module = f.Item(35899);
        long extraModule = f.Item(45009);
        var docked = f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        var exit = Assert.IsType<StationUndockPoint>(docked.Home.UndockPoint);
        Assert.True(exit.Direction.X > 0.9);
        Assert.InRange(Math.Abs(exit.Direction.Z), 0, 0.001);
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
        CheckPersonalHangar();
        Call(
            "beyonce",
            "MachoBindObject",
            [(long)Fixture.SystemId, new object?[] { "UpdateStateRequest", Array.Empty<object?>(), new Dictionary<string, object?>() }]
        );
        Assert.Equal(
            structure.Id,
            Assert.Single(context.Flight.Updates.Select(update => update.Command).OfType<DestinyCommand.SetState>()).Snapshot.Ego
        );
        Assert.False(context.Flight.InBallpark);
        context.Flight.Updates.Clear();
        Call("structureControl", "TakeControl", [structure.Id]);
        CheckPersonalHangar();
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
            router.Call(context, "structureDocking", "Undock", new object?[] { structure.Id, long.MaxValue }, _ => { })
        );
        var reference = Assert.IsType<BlueObjectReference>(Call("invbroker", "GetInventoryFromId", [structure.Id]).Reply.ToBlue());
        string inventory = context.ResolveReference(reference.Id)!;
        Assert.StartsWith("@structure-inventory:", inventory);
        Assert.Equal(structure.Id, Assert.IsType<InventoryRow>(Call(inventory, "GetSelfInvItem", []).Reply).Id);
        var visible = Assert.IsType<BlueList<InventoryRow>>(Call(inventory, "List", []).Reply).Items;
        Assert.Contains(visible, item => item.Id == docked.ShipId && item.FlagId == (int)InventoryFlag.Hangar);
        var shipInventory = Assert.IsType<BlueObjectReference>(Call("invbroker", "GetInventoryFromId", [docked.ShipId]).Reply.ToBlue());
        Assert.Contains(
            Assert.IsType<BlueList<InventoryRow>>(Call(context.ResolveReference(shipInventory.Id)!, "List", []).Reply).Items,
            item => item.Id == core
        );
        Call(inventory, "Add", [core, docked.ShipId], new() { ["flag"] = 4L, ["qty"] = null });
        Assert.Contains(Assert.IsType<BlueList<InventoryRow>>(Call(inventory, "List", [4L]).Reply).Items, item => item.Id == core);
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
            6000,
            f.Structures.Inventory(f.PilotId, structure.Id).Single(item => item.FlagId == StructureRules.FuelBayFlag).Quantity
        );
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(15));
        scope.ServiceProvider.GetRequiredService<StructureControlHandler>().Advance(context);
        Assert.Equal(StructureState.ShieldVulnerable, context.Notifications.OfType<StructureHangarChanged>().Last().Structure.State);
        Assert.Throws<InvalidDataException>(() => f.Structures.FitServices(f.PilotId, structure.Id, [new(module), new(core)]));
        Assert.Empty(f.Structures.Read(f.PilotId, structure.Id)!.Modules);
        // Native fittingUtil.TryFit batches modules with flagAutoFit, then Godma activates the online effect.
        Call(inventory, "MultiAdd", [new List<object?> { module, extraModule }, docked.ShipId], new() { ["flag"] = 0L, ["qty"] = null });
        Assert.Equal(
            new[] { StructureRules.FirstServiceSlot, StructureRules.FirstServiceSlot + 1 },
            f.Structures.Read(f.PilotId, structure.Id)!.Modules.Select(m => m.Slot)
        );
        Call("dogmaIM", "Activate", [module, "online", null, 0L]);
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, inventory, "Add", new object?[] { module, structure.Id }, _ => { }, new() { ["flag"] = 4L })
        );
        Call("dogmaIM", "Deactivate", [module, "online"]);
        Call(inventory, "Add", [module, structure.Id], new() { ["flag"] = 4L, ["qty"] = null });
        Assert.DoesNotContain(f.Structures.Read(f.PilotId, structure.Id)!.Modules, fitted => fitted.ItemId == module);
        Call(inventory, "Add", [module, structure.Id], new() { ["flag"] = 0L });
        Call("dogmaIM", "Activate", [module, "online", structure.Id, 0L]);
        Call("dogmaIM", "Activate", [extraModule, "online", structure.Id, 0L]);
        Assert.Contains(context.Notifications, n => n is StructureServicesChanged);
        var ready = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.Equal(StructureState.ShieldVulnerable, ready.State);
        Assert.True(ready.Modules.Single(m => m.ItemId == module).Online);
        var fittedInfo = Assert.IsType<DogmaAllInfo>(Call("dogmaIM", "GetAllInfo", [true, true, true]).Reply);
        Assert.True(fittedInfo.ShipInfo[structure.Id].Attributes[Eden.Gameplay.Dogma.DogmaAttributeId.CpuLoad] > 0);
        Assert.Contains(StructureService.Reprocessing, ready.Services);
        Assert.Contains(StructureService.MoonMining, ready.Services);
        var drillInfo = Assert.IsType<DogmaItemInfo<double>>(Call("dogmaIM", "ItemGetInfo", [extraModule]).Reply);
        Assert.True(drillInfo.Attributes[Eden.Gameplay.Dogma.DogmaAttributeId.IsOnline] > 0);
        Assert.Equal(
            (double)ready.Modules.Single(m => m.ItemId == extraModule).HourlyFuel!.Value,
            drillInfo.Attributes[Eden.Gameplay.Dogma.DogmaAttributeId.ServiceModuleFuelAmount]
        );
        Call("ship", "GetShipConfiguration", [structure.Id]);
        Assert.NotEmpty(
            Assert.IsType<BlueMap<long, double>>(Call("moonExtractions", "GetMoonResourcesForStructure", [structure.Id]).Reply).Entries
        );
        var extraction = Assert
            .IsType<MoonExtractionReply>(
                Call("moonExtractions", "StartNewExtraction", [structure.Id, MoonMiningRules.MinimumExtractionSeconds, false]).Reply
            )
            .Extraction;
        Assert.Equal(structure.Id, extraction.StructureId);
        Assert.Single(
            Assert
                .IsType<BlueList<MoonExtractionReply>>(Call("moonExtractions", "GetExtractionsForCorp", [(long)f.CorporationId]).Reply)
                .Items
        );
        Call("moonExtractions", "CancelExtraction", [structure.Id]);
        Assert.Null(
            Assert
                .IsType<MoonExtractionWindow>(Call("moonExtractions", "GetExtractionAndEventIDFromStructureID", [structure.Id]).Reply)
                .Extraction
        );
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
        Call("structureControl", "TakeControl", [structure.Id]);
        var systemInventory = context.Reference("invbroker", PilotBindingLifetime.Location);
        var beforeUndock = context.Pilot;
        Call("structureDocking", "Undock", [structure.Id, null], new() { ["ignoreContraband"] = false });
        PilotSessionOutput output = new(context, 1000001, 51, _ => Task.CompletedTask, _ => { });
        await output.AnnounceTransitionAsync(beforeUndock, structure.Id);
        Assert.Equal("invbroker", context.ResolveReference(systemInventory.Id));
        Assert.Null(context.ControlledStructureId);
        Assert.Equal(docked.ShipId, context.Pilot.ShipId);
        Assert.NotNull(context.Pilot.Position);
        Assert.False(context.Pilot.InStructure);
        Assert.Equal(
            docked.ShipId,
            Assert.Single(context.Flight.Updates.Select(update => update.Command).OfType<DestinyCommand.SetState>()).Snapshot.Ego
        );
        Assert.Null(f.Db.Services.GetRequiredService<SharedStructureControl>().Pilot(structure.Id));
        Assert.NotNull(f.Db.Store.FindPilot(1000001)!.Position);
        Assert.Equal(Fixture.SystemId, f.Db.Store.FindPilot(1000001)!.SystemId);
        Assert.Equal(StationUndocking.Launch(docked.ShipId, docked.Home, docked.Flight).Position, context.Pilot.Position);

        PilotCallResult.Success Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            Assert.IsType<PilotCallResult.Success>(router.Call(context, service, method, args, _ => { }, keywords));

        void Install(long item, int flag) =>
            Call(inventory, "MultiAdd", [new List<object?> { item }, docked.ShipId], new() { ["flag"] = (long)flag });

        void CheckPersonalHangar()
        {
            var hangar = Assert.IsType<BlueObjectReference>(Call("invbroker", "GetInventory", [10014L, null]).Reply.ToBlue());
            string service = context.ResolveReference(hangar.Id)!;
            Assert.Equal("@hangar", service);
            var row = Assert.IsType<InventoryRow>(Call(service, "GetSelfInvItem", []).Reply);
            Assert.Equal(structure.Id, row.Id);
            Assert.Equal(structure.Profile.TypeId, row.TypeId);
            Assert.Equal(structure.CorporationId, row.OwnerId);
            var items = Assert.IsType<BlueList<InventoryRow>>(Call(service, "List", [4L]).Reply).Items;
            Assert.Contains(items, item => item.Id == docked.ShipId && item.LocationId == structure.Id);
        }
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
                        new object?[] { "GetInventory", new object?[] { 10014L, null }, new Dictionary<string, object?>() },
                    ]
                ),
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
                PilotServicesTests.Request("beyonce", "MachoBindObject", [(long)Fixture.SystemId, null]),
                PilotServicesTests.Request("structureControl", "TakeControl", [structure.Id]),
                PilotServicesTests.Request(
                    "invbroker",
                    "MachoBindObject",
                    [
                        new object?[] { (long)Fixture.SystemId, 5L },
                        new object?[] { "GetInventory", new object?[] { 10014L, null }, new Dictionary<string, object?>() },
                    ]
                ),
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
                PilotServicesTests.Request(
                    "structureDocking",
                    "Undock",
                    [structure.Id, docked.ShipId],
                    keywords: new() { ["ignoreContraband"] = false }
                ),
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
