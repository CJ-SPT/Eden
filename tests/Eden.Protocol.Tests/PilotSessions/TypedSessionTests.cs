using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class TypedSessionTests
{
    [Fact]
    public void SignedSettingsAndAddressesPreserveTheirWireForms()
    {
        Equal(new object?[] { new byte[] { 1, 2 }, new byte[] { 3 } }, new SignedSettings([1, 2], [3]));
        const string address = "carbon.common.script.net.machoNetAddress.MachoAddress";
        Equal(new BlueInstance(address, new object?[] { 1, 10001, null, null }), new MachoAddress.Node(10001));
        Equal(new BlueInstance(address, new object?[] { 2, 42, null, null }), new MachoAddress.Client(42));
        Equal(
            new BlueInstance(
                address,
                new object?[]
                {
                    4,
                    "OnExample",
                    new List<object?> { 42L },
                    "clientID",
                }
            ),
            new MachoAddress.Broadcast("OnExample", 42)
        );
    }

    [Fact]
    public void AccountAndLoginSessionKeepDictionaryOrderAndNumericValues()
    {
        Dictionary<string, object?> account = new()
        {
            ["userid"] = 1000001L,
            ["role"] = PilotLogin.PlayerRole,
            ["languageID"] = "EN",
            ["userType"] = 1,
        };
        Equal(account, new AccountSessionState(1000001));
        Equal(
            new Dictionary<string, object?>
            {
                ["session_init"] = account,
                ["session_id"] = 42L,
                ["sessionID"] = 42L,
                ["clientID"] = 42L,
                ["access_token"] = "",
                ["computer_hash"] = "",
                ["client_hash"] = "Unknown",
            },
            new LoginSession(1000001, 42)
        );
    }

    [Fact]
    public void SuccessfulNoneIsDistinctFromUnsupportedAndFailures()
    {
        var none = Assert.IsType<PilotCallResult.Success>(PilotCallResult.FromLegacy(null));
        Equal(null, none.Reply);
        Assert.IsType<PilotCallResult.UnsupportedCall>(PilotCallResult.FromLegacy(PilotCallRouter.Unsupported));
        Assert.Equal(new PilotCallResult.Failure("reason"), PilotCallResult.FromLegacy(new PilotCallRouter.Error("reason")));
        var named = Assert.IsType<PilotCallResult.NamedFailure>(
            PilotCallResult.FromLegacy(new PilotCallRouter.UserError("CustomInfo", new() { ["info"] = "reason" }))
        );
        Equal(new Dictionary<string, object?> { ["info"] = "reason" }, named.Values);
        Assert.Equal("CustomInfo", named.Name);
    }

    [Fact]
    public void RequestEnvelopeNamesFieldsWithoutChangingDynamicArguments()
    {
        var request = PilotCallRequest.Read(
            BlueMarshal.Encode(
                new object?[]
                {
                    null,
                    "List",
                    new object?[] { 4L },
                    new Dictionary<string, object?> { ["flag"] = 5L },
                }
            )
        );
        Assert.Null(request.ObjectId);
        Assert.Equal("List", request.Method);
        Assert.Equal(4L, Assert.Single(Assert.IsType<object?[]>(request.Arguments)));
        Assert.Equal(5L, request.Keywords!["flag"]);
        Assert.False(request.IsBindingWithoutCall);
        Assert.Throws<InvalidDataException>(() => PilotCallRequest.Read(BlueMarshal.Encode(new object?[] { "List" })));
    }

    [Fact]
    public void TypedCollectionsPreserveListAndDictionaryForms()
    {
        Equal(new List<object?> { 1L, 2L }, new BlueList<long>([1, 2]));
        Equal(new Dictionary<long, object?> { [4] = 3d }, new BlueMap<long, double>(new Dictionary<long, double> { [4] = 3 }));
        Equal(new Dictionary<string, object?>(), new BlueMap<string, long>(new Dictionary<string, long>()));
    }

    private static void Equal(object? original, IBlueWritable model) =>
        Assert.Equal(BlueMarshal.Encode(original), BlueMarshal.Encode(model));
}
