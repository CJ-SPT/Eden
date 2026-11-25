using Eden.Data.World.Structures;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureDeploymentDefaultsTests
{
    [Fact]
    public void NativeFirstBoundReinforcementDefaultsRoundTripIntoResidentDeploymentAndCheckpoint()
    {
        using StructureTests.Fixture fixture = new(liveWorld: true);
        using var scope = fixture.Db.Services.CreateScope();
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        object?[] first = ["GetStructureReinforceDefault", Array.Empty<object?>()];
        var result = Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "corpRegistry", "MachoBindObject", new object?[] { (long)fixture.CorporationId, first }, _ => { })
        );
        var reply = Assert.IsType<BoundPilotReply>(result.Reply);
        Assert.Equal(new object?[] { 255L, 18L }, Defaults(reply.Reply));
        Assert.Empty(fixture.Structures.List(fixture.PilotId));
        Assert.Empty(context.Notifications);
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "corpRegistry", "GetStructureReinforceDefault", new object?[] { 1L }, _ => { })
        );
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(
                context,
                "corpRegistry",
                "GetStructureReinforceDefault",
                Array.Empty<object?>(),
                _ => { },
                new() { ["unexpected"] = true }
            )
        );
        object?[] setter = ["SetStructureReinforceDefault", new object?[] { 7L }];
        var updated = Assert.IsType<BoundPilotReply>(
            Assert
                .IsType<PilotCallResult.Success>(
                    router.Call(context, "corpRegistry", "MachoBindObject", new object?[] { (long)fixture.CorporationId, setter }, _ => { })
                )
                .Reply
        );
        Assert.Null(updated.Reply.ToBlue());
        var defaults = Defaults(
            Assert
                .IsType<PilotCallResult.Success>(
                    router.Call(context, "corpRegistry", "GetStructureReinforceDefault", Array.Empty<object?>(), _ => { })
                )
                .Reply
        );
        Assert.Equal(new object?[] { 255L, 7L }, defaults);

        long item = fixture.Item(35832);
        object?[] anchor = [item, 0d, 0d, 0d, (long)fixture.CorporationId, "Native defaults", "", defaults[0], defaults[1], null];
        Assert.IsType<PilotCallResult.Success>(router.Call(context, "structureDeployment", "Anchor", anchor, _ => { }));
        var deployed = Assert.Single(fixture.Structures.List(fixture.PilotId));
        Assert.Equal(255, deployed.ReinforceWeekday);
        Assert.Equal(7, deployed.ReinforceHour);
        Assert.DoesNotContain(
            fixture.Db.Services.GetRequiredService<IPilotInventory>().List(fixture.PilotId, fixture.ShipId),
            entry => entry.Id == item
        );
        Assert.Same(
            PilotCallResult.None,
            router.Call(context, "corpRegistry", "SetStructureReinforceDefault", new object?[] { 21L }, _ => { })
        );
        Assert.Equal(7, fixture.Structures.Read(fixture.PilotId, deployed.Id)!.ReinforceHour);
        using var checkpoint = Assert.IsType<WorldSnapshot>(LiveWorld.Find(fixture.Db.Path)!.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        var saved = Assert.Single(reopened.Set<SavedStructure>());
        Assert.Equal(255, saved.ReinforceWeekday);
        Assert.Equal(7, saved.ReinforceHour);
        Assert.Equal(21, reopened.Set<SavedStructureReinforcementDefault>().Find(fixture.CorporationId)!.Hour);
    }

    [Fact]
    public void NativeCorporationReinforcementDefaultsUseCurrentDirectorAuthority()
    {
        using StructureTests.Fixture fixture = new(liveWorld: true);
        const long guest = 90000002;
        var membership = fixture.Db.Services.GetRequiredService<ICorporationMembership>();
        var invitation = membership.Invite(fixture.PilotId, guest);
        using CorporationTestClient ceo = new(fixture.Db, 1000001);
        using CorporationTestClient member = new(fixture.Db, 1000002);
        Assert.Null(ceo.Reply("SetStructureReinforceDefault", [0L]));
        Assert.Equal(new object?[] { 255L, 18L }, member.Reply("GetStructureReinforceDefault", []));
        Assert.IsType<PilotCallResult.Failure>(member.Call("SetStructureReinforceDefault", [23L]));

        membership.Update(
            guest,
            new(invitation.Id, guest, fixture.CorporationId, "", CorporationApplicationStatus.Joined, "", invitation.AppliedAt)
        );
        var roles = fixture.Db.Services.GetRequiredService<ICorporationRoles>();
        SetRole(CorporationRole.StationManager);
        Assert.Equal(new object?[] { 255L, 0L }, member.Reply("GetStructureReinforceDefault", []));
        Assert.IsType<PilotCallResult.Failure>(member.Call("SetStructureReinforceDefault", [23L]));
        SetRole(CorporationRole.Director);
        Assert.Null(member.Reply("SetStructureReinforceDefault", [23L]));
        foreach (object? invalid in new object?[] { -1L, 24L, true, null, 8d })
        {
            Assert.IsType<PilotCallResult.Failure>(member.Call("SetStructureReinforceDefault", [invalid]));
        }
        Assert.IsType<PilotCallResult.Failure>(member.Call("SetStructureReinforceDefault", [7L], new() { ["corporationID"] = 123L }));
        SetRole(CorporationRole.StationManager);
        Assert.IsType<PilotCallResult.Failure>(member.Call("SetStructureReinforceDefault", [7L]));
        Assert.Equal(new object?[] { 255L, 23L }, ceo.Reply("GetStructureReinforceDefault", []));

        void SetRole(CorporationRole role) =>
            roles.UpdateMembers(fixture.PilotId, [new(guest, null, null, null, new((long)role, 0, 0, 0, 0, 0, 0, 0), null, 0)]);
    }

    [Fact]
    public async Task Version239AddsTheCorporationDefaultWithoutChangingDeployedStructureTiming()
    {
        using StructureTests.Fixture fixture = new(liveWorld: true);
        var deployed = fixture
            .Structures.Deploy(
                fixture.PilotId,
                new(fixture.Item(35832), StructureTests.Fixture.Point, 0, "Existing schedule", ReinforceHour: 9)
            )
            .Structure;
        await fixture.Db.Services.GetRequiredService<LiveWorld>().FlushAsync();
        fixture.Db.Services.Dispose();
        using (WorldDbContext saved = new(fixture.Db.Path))
        {
            saved.Database.ExecuteSqlRaw("DROP TABLE WorldStructureReinforcementDefault; PRAGMA user_version=239;");
        }
        WorldDatabase database = new(fixture.Db.Path);
        WorldInitialization.Initialize(database);
        using var reopened = ServerServices.Build(database, fixture.Clock, liveWorld: true);
        Assert.Equal(18, reopened.GetRequiredService<IStructureReinforcementDefaults>().ReadHour(fixture.PilotId));
        Assert.Equal(9, reopened.GetRequiredService<IPilotStructures>().Read(fixture.PilotId, deployed.Id)!.ReinforceHour);
    }

    private static object?[] Defaults(BlueModel reply) => Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(reply)));

    [Fact]
    public void InvalidReinforcementTimingPreservesThePackagedHull()
    {
        using StructureTests.Fixture fixture = new(liveWorld: true);
        long item = fixture.Item(35832);
        foreach (int weekday in new[] { -1, 7, 254, 256 })
        {
            Assert.Throws<InvalidDataException>(() =>
                fixture.Structures.Deploy(
                    fixture.PilotId,
                    new(item, StructureTests.Fixture.Point, 0, "Invalid timing", ReinforceWeekday: weekday)
                )
            );
        }
        foreach (int hour in new[] { -1, 24 })
        {
            Assert.Throws<InvalidDataException>(() =>
                fixture.Structures.Deploy(
                    fixture.PilotId,
                    new(item, StructureTests.Fixture.Point, 0, "Invalid timing", ReinforceWeekday: 255, ReinforceHour: hour)
                )
            );
        }
        Assert.Empty(fixture.Structures.List(fixture.PilotId));
        var hull = Assert.Single(
            fixture.Db.Services.GetRequiredService<IPilotInventory>().List(fixture.PilotId, fixture.ShipId),
            entry => entry.Id == item
        );
        Assert.Equal(1, hull.Quantity);
    }
}
