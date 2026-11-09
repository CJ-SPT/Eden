using Eden.Gameplay.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CharacterSearchTests
{
    [Theory]
    [InlineData(CharacterSearchMatch.PartialTerms, "eden pil", "Eden Pilot", true)]
    [InlineData(CharacterSearchMatch.PartialTerms, "pi", "Eden Pilot", false)]
    [InlineData(CharacterSearchMatch.PartialTerms, "pilot", "Eden Pilot", true)]
    [InlineData(CharacterSearchMatch.ExactTerms, "pilot eden", "Eden Pilot", true)]
    [InlineData(CharacterSearchMatch.ExactTerms, "pil", "Eden Pilot", false)]
    [InlineData(CharacterSearchMatch.ExactPhrase, "eden pilot", "Eden Pilot", true)]
    [InlineData(CharacterSearchMatch.ExactPhrase, "den", "Eden Pilot", false)]
    [InlineData(CharacterSearchMatch.ExactPhrase, "pilot", "Eden Pilot", true)]
    [InlineData(CharacterSearchMatch.ExactPhrase, "pilot eden", "Eden Pilot", false)]
    [InlineData(CharacterSearchMatch.ExactName, "Eden Pilot", "eden pilot", true)]
    [InlineData(CharacterSearchMatch.ExactName, "pilot", "Eden Pilot", false)]
    [InlineData(CharacterSearchMatch.ExactPhrase, "学習", "Ada 学習 Pilot", true)]
    public void NativeMatchingModesRespectTermsOrderBoundariesAndUnicode(
        CharacterSearchMatch match,
        string query,
        string name,
        bool result
    ) => Assert.Equal(result, CharacterSearchRules.Matches(query, name, match));

    [Fact]
    public void NativeRecipientSearchReturnsResolvableLocalIdsInGroupedAndFlatForms()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        object?[] args = [new BlueUnicode("Eden Wing"), new List<object?> { 2L }];
        Assert.Same(PilotCallResult.Unsupported, client.Call("QuickQuery", args, "search"));
        client.Refresh();
        var ids = Assert.IsType<List<object?>>(
            Reply(
                client.Call(
                    "QuickQuery",
                    args,
                    "search",
                    new()
                    {
                        ["exact"] = 2L,
                        ["hideNPC"] = 1L,
                        ["onlyAltName"] = false,
                    }
                )
            )
        );
        Assert.Equal(MailTestData.Recipient, Assert.Single(ids));
        var grouped = Assert.IsType<Dictionary<long, object?>>(
            Reply(client.Call("Query", ["Eden", new List<object?> { 2L }], "search", new() { ["exact"] = 2L }))
        );
        Assert.Equal(new List<object?> { MailTestData.Sender, MailTestData.Recipient }, Assert.IsType<List<object?>>(grouped[2]));
        Assert.Empty(Assert.IsType<List<object?>>(Reply(client.Call("QuickQuery", args, "search", new() { ["onlyAltName"] = true }))));
        Assert.Empty(
            Assert.IsType<Dictionary<string, object?>>(Reply(client.Call("Query", ["missing", new List<object?> { 2L }], "search")))
        );
    }

    [Fact]
    public void InvalidSearchShapesCategoriesModesAndKeywordsReject()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        foreach (
            var args in new object?[][]
            {
                ["", new List<object?> { 2L }],
                [new string('a', 101), new List<object?> { 2L }],
                ["Eden", new List<object?> { 14L }],
                ["Eden", new List<object?> { 2L, 2L }],
                ["Eden"],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(client.Call("QuickQuery", args, "search"));
        }
        Assert.IsType<PilotCallResult.Failure>(
            client.Call("Query", ["Eden", new List<object?> { 2L }], "search", new() { ["exact"] = 4L })
        );
        Assert.IsType<PilotCallResult.Failure>(
            client.Call("Query", ["Eden", new List<object?> { 2L }], "search", new() { ["ownerID"] = 1L })
        );
    }

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));
}
