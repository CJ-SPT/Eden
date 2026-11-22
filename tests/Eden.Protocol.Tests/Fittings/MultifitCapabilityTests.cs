using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Client = Eden.Protocol.Tests.PilotSessions.CourierServiceTests.Client;

namespace Eden.Protocol.Tests.Fittings;

public sealed class MultifitCapabilityTests
{
    [Theory]
    [InlineData(false, PilotRole.Player)]
    [InlineData(true, PilotRole.Player)]
    [InlineData(false, PilotRole.Developer)]
    [InlineData(true, PilotRole.Developer)]
    public void NativeCapabilityChecksRoleAndDockingWithoutChangingAssetsOrNotifying(bool inSpace, PilotRole roles)
    {
        using TestWorld db = new(emptyWallets: false);
        using var scope = db.Services.CreateScope();
        var pilot = db.Store.FindPilot(1000001)!;
        if (inSpace)
        {
            db.Store.SetPosition(pilot.ShipId, new(1_000_000, 0, 0));
            pilot = db.Store.FindPilot(1000001)!;
        }
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        )
        {
            Roles = (long)roles,
        };
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.IsType<PilotCallResult.UnsupportedCall>(Call());
        context.Select(pilot);
        string before = Assets();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var reply = Assert.IsType<PilotCallResult.Success>(Call(keywords: attempt == 0 ? null : []));
            Assert.Equal(
                !inSpace && roles == PilotRole.Developer,
                Assert.IsType<bool>(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply)))
            );
        }
        Assert.IsType<PilotCallResult.Failure>(Call(args: [pilot.PilotId]));
        Assert.IsType<PilotCallResult.Failure>(Call(keywords: new() { ["typeID"] = 601L }));
        Assert.IsType<PilotCallResult.Failure>(Call(method: "SlashCmd", args: ["/giveitem 601 1"]));
        Assert.IsType<PilotCallResult.Failure>(Call(method: "MoveMeTo", args: [pilot.StationId]));
        Assert.IsType<PilotCallResult.UnsupportedCall>(Call(service: "unrelatedService"));
        Assert.Equal(before, Assets());
        Assert.Empty(context.Notifications);
        Assert.Empty(context.NotificationsBeforeFlight);

        PilotCallResult Call(
            string service = "publicQaToolsServer",
            string method = "CanGiveItemForMultifit",
            object?[]? args = null,
            Dictionary<string, object?>? keywords = null
        ) => router.Call(context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args ?? [])), _ => { }, keywords);

        string Assets()
        {
            using WorldDbContext world = new(db.Path);
            return JsonSerializer.Serialize(
                new
                {
                    Ships = world.Ships.OrderBy(s => s.Id).ToArray(),
                    Items = world.Set<OwnedItem>().OrderBy(i => i.Id).ToArray(),
                    Wallets = world.Database.SqlQueryRaw<long>("SELECT Cents AS Value FROM WorldWallet ORDER BY PilotId").ToArray(),
                }
            );
        }
    }

    [Fact]
    public async Task NativeCreateMissingDeliversTheWholeBatchOrLeavesTheHangarUnchanged()
    {
        TestClock clock = new();
        using TestWorld db = new(liveWorld: true, clock: clock);
        using Client gm = new(db, clock, 1000001, PilotRole.WorldMod);
        gm.Refresh();
        var pilot = gm.Context.Pilot;
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        var before = inventory.List(pilot.PilotId, pilot.DockedLocationId);
        using (Client player = new(db, clock, 1000001))
        {
            player.Refresh();
            Assert.IsType<PilotCallResult.Failure>(player.Call("SlashCmd", ["/giveitem 601|3"], "publicQaToolsServer"));
        }
        Assert.IsType<PilotCallResult.Failure>(gm.Call("SlashCmd", ["/create 601 2"], "publicQaToolsServer"));
        Assert.IsType<PilotCallResult.Failure>(gm.Call("SlashCmd", ["/giveitem 601|3,2147483647|1"], "publicQaToolsServer"));
        Assert.Equal(before, inventory.List(pilot.PilotId, pilot.DockedLocationId));
        gm.Context.Notifications.Clear();
        Assert.Equal(PilotCallResult.None, gm.Call("SlashCmd", ["/giveitem 601|3,3651|4"], "publicQaToolsServer"));
        var now = inventory.List(pilot.PilotId, pilot.DockedLocationId);
        Assert.Equal(
            3,
            now.Where(row => row.TypeId == 601).Sum(row => row.Quantity) - before.Where(row => row.TypeId == 601).Sum(row => row.Quantity)
        );
        Assert.Equal(
            4,
            now.Where(row => row.TypeId == 3651).Sum(row => row.Quantity) - before.Where(row => row.TypeId == 3651).Sum(row => row.Quantity)
        );
        await LiveWorld.Find(db.Path)!.FlushAsync();
        using WorldDbContext persisted = new(db.Path);
        Assert.Contains(persisted.Set<OwnedItem>(), row => row.OwnerId == pilot.PilotId && row.TypeId == 601 && row.Quantity >= 3);
    }
}
