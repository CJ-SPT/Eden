using Eden.Gameplay.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationWalletStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionAdvertisesTheIndividualWalletBitsRequiredByTheNativeIskHeader(bool director)
    {
        using TestWorld world = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        if (director)
        {
            Assign(world, CorporationRole.Director);
        }
        long account = director ? 1000002 : 1000001;
        var pilot = world.Store.FindPilot(account)!;
        var delta = Fields(PilotSessionWire.SessionChange(account, 42, pilot));
        long roles = Assert.IsType<long>(BlueRead.Tuple(delta["corprole"], 2)[1]);
        // walletUtil.AmAccountantOrJuniorAccountant tests these bits directly.
        Assert.NotEqual(0, roles & (long)(CorporationRole.Accountant | CorporationRole.JuniorAccountant));
        foreach (int key in Enumerable.Range(CorporationWalletRules.MasterKey, CorporationWalletRules.DivisionCount))
        {
            // HaveAccessToCorpWalletDivision has no Director fallback.
            Assert.NotEqual(0, roles & ((long)CorporationRole.AccountCanTake1 << (key - CorporationWalletRules.MasterKey)));
        }
        Assert.Equal(CorporationRoleRules.KnownRoles, BlueRead.Tuple(delta["rolesAtAll"], 2)[1]);
        foreach (string field in new[] { "rolesAtHQ", "rolesAtBase", "rolesAtOther" })
        {
            Assert.Equal(CorporationRoleRules.LocationalRoles, BlueRead.Tuple(delta[field], 2)[1]);
        }
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        wallet.Transfer(90000001, new(false, 1000, corporation, 1000, 12345, "Header balance"));
        using CorporationTestClient client = new(world, account);
        var response = client.CallService("account", "GetCashBalance", [1L], new() { ["accountKey"] = null });
        Assert.Equal(
            123.45,
            Assert.IsType<double>(BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(response).Reply)))
        );
        Assert.Equal(CorporationRules.CeoRoles, pilot.Identity.CorporationRoles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryAndLimitedMembersKeepTheirActualPermissions(bool limited)
    {
        using TestWorld world = new();
        CorporationAdministrationTests.CreateAndJoin(world);
        var assigned = limited ? CorporationRole.AccountCanTake2 : CorporationRole.NoRole;
        Assign(world, assigned);
        var pilot = world.Store.FindPilot(1000002)!;
        var delta = Fields(PilotSessionWire.SessionChange(1000002, 42, pilot));
        Assert.Equal((long)assigned, BlueRead.Tuple(delta["corprole"], 2)[1]);
        Assert.Equal((long)assigned, BlueRead.Tuple(delta["rolesAtAll"], 2)[1]);
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        Assert.Throws<InvalidDataException>(() => wallet.Balance(90000002, 1000));
        Assert.Equal(limited ? 1 : 0, wallet.Divisions(90000002).Count);
        if (limited)
        {
            Assert.Equal(1001, Assert.Single(wallet.Divisions(90000002)).Key);
        }
    }

    [Fact]
    public void RevokingDirectorClearsEveryExpandedSessionPermission()
    {
        using TestWorld world = new();
        CorporationAdministrationTests.CreateAndJoin(world);
        Assign(world, CorporationRole.Director);
        var before = world.Store.FindPilot(1000002)!;
        Assign(world, CorporationRole.NoRole);
        var after = world.Store.FindPilot(1000002)!;
        var delta = Fields(PilotSessionWire.SessionChange(1000002, 42, after, before));
        Assert.Equal(new object?[] { CorporationRoleRules.KnownRoles, 0L }, delta["corprole"]);
        Assert.Equal(new object?[] { CorporationRoleRules.KnownRoles, 0L }, delta["rolesAtAll"]);
        foreach (string field in new[] { "rolesAtHQ", "rolesAtBase", "rolesAtOther" })
        {
            Assert.Equal(new object?[] { CorporationRoleRules.LocationalRoles, 0L }, delta[field]);
        }
        Assert.Throws<InvalidDataException>(() => world.Services.GetRequiredService<ICorporationWallet>().Balance(90000002, 1000));
    }

    private static void Assign(TestWorld world, CorporationRole role) =>
        world
            .Services.GetRequiredService<ICorporationRoles>()
            .UpdateMembers(90000001, [new(90000002, null, null, null, new((long)role, 0, 0, 0, 0, 0, 0, 0), null, 0)]);

    private static Dictionary<string, object?> Fields(BlueInstance packet)
    {
        var decoded = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(packet)));
        return BlueRead.Fields(BlueRead.Tuple(BlueRead.Tuple(BlueRead.Tuple(decoded.State, 14)[4], 3)[1], 2)[1]);
    }
}
