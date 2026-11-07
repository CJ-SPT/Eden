using Eden.Gameplay.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server.Launcher;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Launcher;

[CollectionDefinition("DeveloperInvitation", DisableParallelization = true)]
public sealed class DeveloperInvitationCollection;

[Collection("DeveloperInvitation")]
public sealed class DeveloperAccountTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OptionalQaSpawnOverrideIsEmptyOnlyForQaAccounts(bool developer)
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var pilot = world.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            _ => throw new InvalidOperationException("This check does not start flight."),
            42,
            SignedSettings.Empty
        )
        {
            Roles = (long)(developer ? PilotRole.Developer : PilotRole.Player),
        };
        var handler = scope.ServiceProvider.GetRequiredService<CharacterSelectionHandler>();
        var result = handler.Call(context, "charUnboundMgr", "GetQAStarterSystemIDs", Array.Empty<object>(), null);
        if (developer)
        {
            var success = Assert.IsType<PilotCallResult.Success>(result);
            Assert.Equal(BlueMarshal.Encode(new List<object?>()), BlueMarshal.Encode(success.Reply.ToBlue()));
        }
        else
        {
            Assert.IsType<PilotCallResult.UnsupportedCall>(result);
        }
    }

    [Fact]
    public void InvitationControlsDeveloperRolesAndBothSessionMessagesPreserveThem()
    {
        string? previous = Environment.GetEnvironmentVariable("EDEN_DEVELOPER_INVITE_CODE");
        try
        {
            using TestWorld world = new();
            var store = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
            store.Initialize();
            const string password = "developer test password";
            Environment.SetEnvironmentVariable("EDEN_DEVELOPER_INVITE_CODE", null);
            Assert.Throws<LauncherException>(() => store.Register(new("Developer", password, true, "invitation")));
            Environment.SetEnvironmentVariable("EDEN_DEVELOPER_INVITE_CODE", "invitation");
            Assert.Throws<LauncherException>(() => store.Register(new("Developer", password, true, "wrong")));
            var account = store.Register(new("Developer", password, true, "invitation"));
            Assert.True(account.Developer);
            Assert.Equal(AccountCloneGrade.Omega, world.Store.ReadAccount(account.AccountId).CloneGrade);
            Assert.Equal((long)PilotRole.Developer, store.ReadRoles(account.AccountId));
            var session = store.SignIn(new("Developer", password, true));
            Assert.True(session.Developer);
            Assert.True(store.Refresh(session.RefreshToken!).Developer);
            var state = BlueRead.Fields(new AccountSessionState(account.AccountId, store.ReadRoles(account.AccountId)).ToBlue());
            Assert.Equal((long)PilotRole.Developer, state["role"]);
            var login = BlueRead.Fields(new LoginSession(account.AccountId, 1, (long)PilotRole.Developer).ToBlue());
            var initial = Assert.IsType<AccountSessionState>(login["session_init"]);
            Assert.Equal((long)PilotRole.Developer, initial.Roles);
            Assert.NotEqual(0UL, (ulong)PilotRole.Developer & 0x959C3FFF7EFFFFDFUL);
            store.SetPassword("Developer", password + " reset");
            Assert.Equal((long)PilotRole.Developer, store.ReadRoles(account.AccountId));
            var player = store.Register(new("Standard", password, false, "invitation"));
            Assert.Equal((long)PilotRole.Player, store.ReadRoles(player.AccountId));
            var reopened = WorldInitialization.Open(new WorldDatabase(world.Path));
            Assert.Equal(AccountCloneGrade.Omega, reopened.ReadAccount(account.AccountId).CloneGrade);
            Assert.Equal(AccountCloneGrade.Omega, reopened.ReadAccount(player.AccountId).CloneGrade);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDEN_DEVELOPER_INVITE_CODE", previous);
        }
    }
}
