using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Protocol.Characters;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class InsiderQaTests
{
    private const long Account = 1000001;
    private const string AssetHolding = "inventoryAssetHoldingMgr";
    private const string Validation = "is_item_validation_enabled";
    private const string SetValidation = "set_item_validation_enabled";
    private const string Failure = "is_item_failure_enabled";
    private const string SetFailure = "set_item_failure_enabled";
    private const string JumpClones = "jumpCloneSvc";
    private const string ResetCooldown = "ResetLastCloneJumpTime";

    [Fact]
    public void NativeAssetHoldingTogglesRoundTripPerConnection()
    {
        using TestWorld db = new(liveWorld: true, simulationObjects: false);
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var pilot = db.Store.FindPilot(Account)!;
        var start = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext qa = new(Account, pilot.Account, [pilot], start.Create, 10, SignedSettings.Empty)
        {
            Roles = (long)PilotRole.QualityAssurance,
        };
        using PilotCallContext otherQa = new(Account, pilot.Account, [pilot], start.Create, 11, SignedSettings.Empty)
        {
            Roles = (long)PilotRole.Developer,
        };
        using PilotCallContext player = new(Account, pilot.Account, [pilot], start.Create, 12, SignedSettings.Empty);

        Assert.Same(PilotCallResult.Unsupported, Call(router, qa, AssetHolding, Validation, []));
        qa.Select(pilot);
        otherQa.Select(pilot);
        player.Select(pilot);
        Assert.Same(PilotCallResult.Unsupported, Call(router, player, AssetHolding, Validation, []));
        Assert.Same(PilotCallResult.Unsupported, Call(router, player, AssetHolding, SetFailure, [true]));
        Assert.True(Assert.IsType<bool>(Reply(Call(router, qa, AssetHolding, Validation, []))));
        Assert.False(Assert.IsType<bool>(Reply(Call(router, qa, AssetHolding, Failure, []))));

        Assert.IsType<PilotCallResult.Failure>(Call(router, qa, AssetHolding, SetValidation, [0L]));
        Assert.IsType<PilotCallResult.Failure>(Call(router, qa, AssetHolding, SetValidation, [false], new() { ["pilotID"] = 5L }));
        Assert.IsType<PilotCallResult.Failure>(Call(router, qa, AssetHolding, Failure, [true]));
        Assert.True(Assert.IsType<bool>(Reply(Call(router, qa, AssetHolding, Validation, []))));
        Assert.False(Assert.IsType<bool>(Reply(Call(router, qa, AssetHolding, Failure, []))));

        Assert.Null(Reply(Call(router, qa, AssetHolding, SetValidation, [false], new() { ["machoVersion"] = 1L })));
        Assert.Null(Reply(Call(router, qa, AssetHolding, SetFailure, [true])));
        Assert.False(Assert.IsType<bool>(Reply(Call(router, qa, AssetHolding, Validation, []))));
        Assert.True(Assert.IsType<bool>(Reply(Call(router, qa, AssetHolding, Failure, []))));
        Assert.True(Assert.IsType<bool>(Reply(Call(router, otherQa, AssetHolding, Validation, []))));
        Assert.False(Assert.IsType<bool>(Reply(Call(router, otherQa, AssetHolding, Failure, []))));

        Assert.Null(Reply(Call(router, qa, AssetHolding, SetValidation, [true])));
        Assert.Null(Reply(Call(router, qa, AssetHolding, SetFailure, [false])));
        Assert.True(Assert.IsType<bool>(Reply(Call(router, qa, AssetHolding, Validation, []))));
        Assert.False(Assert.IsType<bool>(Reply(Call(router, qa, AssetHolding, Failure, []))));
        Assert.Same(PilotCallResult.Unsupported, Call(router, qa, AssetHolding, "unknown", []));
        Assert.Empty(qa.Notifications);
    }

    [Fact]
    public async Task NativeJumpCloneResetClearsOnlyTheSelectedPilotsTimerAndSurvivesCheckpoint()
    {
        using TestWorld db = new();
        long pilotId;
        long otherPilotId;
        long time = DateTime.UtcNow.ToFileTimeUtc();
        using (var services = ServerServices.Build(new(db.Path), liveWorld: true, simulationObjects: false))
        {
            var pilot = db.Store.FindPilot(Account)!;
            pilotId = pilot.PilotId;
            otherPilotId = db.Store.FindPilot(Account + 1)!.PilotId;
            var live = services.GetRequiredService<LiveWorld>();
            using (var seed = live.Economy.Begin(new(pilots: [pilotId, otherPilotId])))
            {
                seed.Add(new SavedJumpCloneTimer { PilotId = pilotId, Time = time });
                seed.Add(new SavedJumpCloneTimer { PilotId = otherPilotId, Time = time });
                seed.Commit();
            }
            await live.FlushAsync();

            using var scope = services.CreateScope();
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            var start = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
            using PilotCallContext qa = new(Account, pilot.Account, [pilot], start.Create, 10, SignedSettings.Empty)
            {
                Roles = (long)PilotRole.Developer,
            };
            using PilotCallContext player = new(Account, pilot.Account, [pilot], start.Create, 11, SignedSettings.Empty);
            Assert.Same(PilotCallResult.Unsupported, Call(router, qa, JumpClones, ResetCooldown, []));
            qa.Select(pilot);
            player.Select(pilot);
            Assert.Same(PilotCallResult.Unsupported, Call(router, player, JumpClones, ResetCooldown, []));
            Assert.IsType<PilotCallResult.Failure>(Call(router, qa, JumpClones, ResetCooldown, [otherPilotId]));
            var clones = services.GetRequiredService<IPilotJumpClones>();
            var before = clones.Read(pilotId);
            Assert.Equal(time, before.LastJump);
            Assert.Empty(qa.Notifications);

            object?[] binding = [(long)pilot.StationId, 15L];
            object?[] firstCall = [ResetCooldown, Array.Empty<object?>(), new Dictionary<string, object?> { ["machoVersion"] = 1L }];
            var reply = Assert.IsType<BoundPilotReply>(
                Assert.IsType<PilotCallResult.Success>(Call(router, qa, JumpClones, "MachoBindObject", [binding, firstCall])).Reply
            );
            Assert.Null(reply.Reply.ToBlue());
            Assert.Single(qa.Notifications.OfType<JumpCloneCacheInvalidated>());
            var after = clones.Read(pilotId);
            Assert.Null(after.LastJump);
            Assert.Equal(before.Clones, after.Clones);
            Assert.Equal(time, clones.Read(otherPilotId).LastJump);

            string service = router.ResolveService(qa, [1L, (long)PilotLogin.NodeId, null, null], reply.Reference.Id);
            Assert.Null(Reply(Call(router, qa, service, ResetCooldown, [])));
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(new(db.Path), liveWorld: true, simulationObjects: false);
        var restored = reopened.GetRequiredService<IPilotJumpClones>();
        Assert.Null(restored.Read(pilotId).LastJump);
        Assert.Equal(time, restored.Read(otherPilotId).LastJump);
    }

    private static PilotCallResult Call(
        PilotCallRouter router,
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    ) => router.Call(context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));
}
