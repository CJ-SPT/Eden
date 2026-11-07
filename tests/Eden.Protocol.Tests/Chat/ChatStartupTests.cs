using System.Security.Cryptography;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Chat;

public sealed class ChatStartupTests
{
    [Fact]
    public void DiscoveryAndUrlSafetyWorkBeforeSelectionWhileChatAuthenticationRemainsGated()
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var pilot = world.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, TimeProvider.System, new(), []),
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        object? Reply(string service, string method) =>
            Assert
                .IsType<PilotCallResult.Success>(
                    router.Call(context, service, method, Array.Empty<object?>(), _ => { }, new() { ["machoVersion"] = 1L })
                )
                .Reply.ToBlue();
        Assert.Equal("eden.chat", BlueMarshal.Text(Reply("XmppChatMgr", "Hostname")));
        Assert.Equal("127.0.0.1", BlueMarshal.Text(Reply("XmppChatMgr", "GetDeprecatedPrefsFallback")));
        string hash = BlueMarshal.Text(Reply("browserLockdownSvc", "GetFlaggedSitesHash"));
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData("[]"u8)), hash);
        Assert.Empty(Assert.IsType<List<object?>>(Reply("browserLockdownSvc", "GetFlaggedSitesList")));
        Assert.Same(
            PilotCallResult.Unsupported,
            router.Call(context, "chatAuthenticationService", "GetAuthenticationToken", Array.Empty<object?>(), _ => { })
        );
        Assert.Null(world.Services.GetRequiredService<ChatRuntime>().User(pilot.PilotId));
        context.Select(pilot);
        Assert.Equal(hash, BlueMarshal.Text(Reply("browserLockdownSvc", "GetFlaggedSitesHash")));
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "browserLockdownSvc", "GetFlaggedSitesList", new object?[] { 1L }, _ => { })
        );
        Assert.Same(PilotCallResult.Unsupported, router.Call(context, "browserLockdownSvc", "Unknown", Array.Empty<object?>(), _ => { }));
    }
}
