using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class TypedCallResultTests
{
    [Fact]
    public void TypedOutcomesKeepFalseZeroAndNoneDistinctFromUnsupported()
    {
        PilotCallResult boolean = false;
        PilotCallResult integer = 0;
        PilotCallResult floating = 0d;
        Assert.Equal(BlueMarshal.Encode(false), Encode(boolean));
        Assert.Equal(BlueMarshal.Encode(0), Encode(integer));
        Assert.Equal(BlueMarshal.Encode(0d), Encode(floating));
        Assert.Equal(BlueMarshal.Encode(null), Encode(PilotCallResult.None));
        Assert.IsType<PilotCallResult.UnsupportedCall>(PilotCallResult.Unsupported);
    }

    [Fact]
    public void RouterKeepsSelectionGatingAndPropagatesFailedNestedBindings()
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var pilot = world.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new FlightSession(world.Store, selected, TimeProvider.System, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        Assert.IsType<PilotCallResult.UnsupportedCall>(router.Call(context, "dogmaIM", "GetAllInfo", Array.Empty<object?>(), _ => { }));
        Assert.Same(
            PilotCallResult.None,
            router.Call(context, "charUnboundMgr", "SelectCharacterID", new object?[] { pilot.PilotId }, _ => { })
        );
        var binding = router.Call(
            context,
            "invbroker",
            "MachoBindObject",
            new object?[] { new object?[] { (long)pilot.StationId, 15L }, new object?[] { "UnimplementedMethod", Array.Empty<object?>() } },
            _ => { }
        );
        Assert.IsType<PilotCallResult.UnsupportedCall>(binding);
        var success = Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "dogmaIM", "GetAllInfo", Array.Empty<object?>(), _ => { })
        );
        Assert.IsType<DogmaAllInfo>(success.Reply);
    }

    [Fact]
    public void StructuredEmptyCompatibilityStatesKeepTheirNativeForms()
    {
        (BlueModel Model, object Value)[] fixtures =
        [
            (
                new ContractLoginState(),
                new BlueInstance(
                    "utillib.KeyVal",
                    new Dictionary<string, object?>
                    {
                        ["assignedToMe"] = new List<object?>(),
                        ["needsAttention"] = new List<object?>(),
                        ["inProgress"] = new List<object?>(),
                    }
                )
            ),
            (
                new ContactListState(),
                new BlueInstance(
                    "utillib.KeyVal",
                    new Dictionary<string, object?> { ["addresses"] = new List<object?>(), ["blocked"] = new List<object?>() }
                )
            ),
            (new SeasonalCampaignState(), new object?[] { null, null, null, 0 }),
            (new AlertCountReply(), new object?[] { 1, 0 }),
            (new LocalSystemState(30000142), new object?[] { 30000142, null }),
            (new DynamicBountyState(), new object?[] { 1d, false }),
            (new JumpTimerState(), new object?[] { null, null, null }),
            (new FighterState(), new object?[] { new List<object?>(), new List<object?>(), new Dictionary<string, object?>() }),
            (new BountiesAndKillRightsState(), new object?[] { new List<object?>(), new List<object?>() }),
            (new AgentJournalState(), new object?[] { new List<object?>(), new List<object?>() }),
            (
                new AchievementState(),
                new Dictionary<string, object?>
                {
                    ["completedDict"] = new Dictionary<string, object?>(),
                    ["eventDict"] = new Dictionary<string, object?>(),
                }
            ),
        ];
        foreach (var (model, original) in fixtures)
        {
            Assert.Equal(BlueMarshal.Encode(original), BlueMarshal.Encode(model));
        }
        Assert.NotEqual(BlueMarshal.Encode(BlueEmpty.Tuple), BlueMarshal.Encode(BlueEmpty.List));
        Assert.NotEqual(BlueMarshal.Encode(BlueEmpty.List), BlueMarshal.Encode(BlueEmpty.Dictionary));
    }

    [Fact]
    public void ErrorValueModelsPreserveNamedFields()
    {
        Assert.Equal(
            BlueMarshal.Encode(new Dictionary<string, object?> { ["info"] = "reason" }),
            BlueMarshal.Encode(new InformationErrorValues("reason"))
        );
        Assert.Equal(
            BlueMarshal.Encode(new Dictionary<string, object?> { ["available"] = 1d, ["volume"] = 2d }),
            BlueMarshal.Encode(new CargoSpaceErrorValues(1, 2))
        );
        Assert.Equal(
            BlueMarshal.Encode(new Dictionary<string, object?> { ["limit"] = 3 }),
            BlueMarshal.Encode(new TargetLimitErrorValues(3))
        );
    }

    [Fact]
    public void LoginAdaptersRequireTheExistingBytesAndCredentialShapes()
    {
        var credentials = LoginCredentials.Read(
            new object?[]
            {
                new byte[] { 1 },
                new Dictionary<string, object?> { ["user_sso_token"] = "ticket" },
            }
        );
        Assert.Equal("ticket", credentials.Ticket);
        Assert.Equal(new byte[] { 1 }, credentials.Challenge);
        Assert.Throws<InvalidDataException>(() =>
            LoginCredentials.Read(
                new object?[]
                {
                    1L,
                    new Dictionary<string, object?> { ["user_sso_token"] = "ticket" },
                }
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            LoginKeyExchange.Read(
                new object?[]
                {
                    "exchange",
                    new Dictionary<string, object?> { ["crypting_sessionkey"] = "invalid", ["crypting_sessioniv"] = new byte[] { 1 } },
                }
            )
        );
    }

    private static byte[] Encode(PilotCallResult result) => BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply);
}
