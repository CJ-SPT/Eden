using System.Collections;
using System.Text.Json;
using Eden.Gameplay.Characters;
using Eden.Protocol.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class PersonalSettingsServiceTests
{
    [Fact]
    public void NativeNotepadRoutesReturnRowsAndPreserveBodiesOnRename()
    {
        using TestWorld db = new();
        using var owner = new Session(db, 1000001);
        using var other = new Session(db, 1000002);
        Assert.Same(PilotCallResult.Unsupported, owner.Call("charMgr", "AddOwnerNote", ["N:Title", "body"]));
        owner.Select();
        other.Select();
        long id = Assert.IsType<long>(Reply(owner.Call("charMgr", "AddOwnerNote", ["N:Title", new BlueUnicode("星間 🚀")])));
        Assert.Null(Reply(owner.Call("charMgr", "EditOwnerNote", [id, "N:Renamed"])));
        var entry = Note(Reply(owner.Call("charMgr", "GetOwnerNote", [id])));
        Assert.Equal("星間 🚀", entry["note"]);
        Assert.Equal("N:Renamed", entry["label"]);
        Assert.Equal(id, entry["noteID"]);
        var list = Assert.IsType<BlueInstance>(Reply(owner.Call("charMgr", "GetOwnerNoteLabels", [])));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", list.Name);
        var state = Assert.IsAssignableFrom<IDictionary>(list.State);
        Assert.Equal(new[] { "noteID", "label" }, Assert.IsAssignableFrom<IList>(state["header"]).Cast<object>().Select(BlueMarshal.Text));
        var line = Assert.Single(Assert.IsAssignableFrom<IList>(state["lines"]).Cast<object>());
        Assert.Equal(new object[] { id, "N:Renamed" }, Assert.IsType<object[]>(line));
        Assert.Equal("", Note(Reply(other.Call("charMgr", "GetOwnerNote", [id])))["note"]);
        Assert.IsType<PilotCallResult.Failure>(other.Call("charMgr", "EditOwnerNote", [id, "N:Stolen", "bad"]));
        Assert.Null(Reply(other.Call("charMgr", "RemoveOwnerNote", [id])));
        Assert.Equal("星間 🚀", Note(Reply(owner.Call("charMgr", "GetOwnerNote", [id])))["note"]);
        Assert.Null(Reply(owner.Call("charMgr", "EditOwnerNote", [id, "N:Renamed", ""])));
        Assert.Equal("", Note(Reply(owner.Call("charMgr", "GetOwnerNote", [id])))["note"]);
        Assert.Null(Reply(owner.Call("charMgr", "RemoveOwnerNote", [id])));
        Assert.Empty(db.Services.GetRequiredService<IPilotNotepad>().List(90000001));
        Assert.Empty(owner.Context.Notifications);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void NativeSharedLinksRoundTripAcrossPilotsAndRejectWrongKeys(int kind)
    {
        using TestWorld db = new();
        using var owner = new Session(db, 1000001);
        using var other = new Session(db, 1000002);
        string service = kind == 0 ? "overviewPresetMgr" : "sharedSettingsMgr";
        string store = kind == 0 ? "StoreLinkAndGetID" : "StoreSettingLinkAndGetID";
        string read = kind == 0 ? "GetStoredPreset" : "GetStoredSharedSetting";
        Dictionary<string, object?> payload = new()
        {
            ["name"] = new BlueUnicode("Travel 星間"),
            ["groups"] = new object[] { 25L, 26L },
            ["enabled"] = true,
            ["missing"] = null,
        };
        object?[] args = kind == 0 ? [payload] : [(long)kind, payload];
        Assert.Same(PilotCallResult.Unsupported, owner.Call(service, store, args));
        owner.Select();
        other.Select();
        var reply = Fields(Reply(owner.Call(service, store, args)));
        long id = Assert.IsType<long>(reply["sqID"]);
        string hash = BlueMarshal.Text(reply["hashvalue"]);
        object[] key = kind == 0 ? [hash, id] : [hash, id, (long)kind];
        string yaml = Assert.IsType<string>(Reply(other.Call(service, read, [key])));
        using var parsed = JsonDocument.Parse(yaml);
        var pairs = parsed.RootElement.EnumerateArray().ToDictionary(p => p[0].GetString()!, p => p[1]);
        Assert.Equal("Travel 星間", pairs["name"].GetString());
        Assert.Equal(25, pairs["groups"][0].GetInt32());
        Assert.True(pairs["enabled"].GetBoolean());
        Assert.Equal(JsonValueKind.Null, pairs["missing"].ValueKind);
        Assert.Equal(id, Fields(Reply(other.Call(service, store, args)))["sqID"]);
        key[0] = "wrong";
        Assert.Null(Reply(other.Call(service, read, [key])));
        key[0] = hash;
        key[1] = long.MaxValue;
        Assert.Null(Reply(other.Call(service, read, [key])));
        Assert.Empty(other.Context.Notifications);
    }

    [Fact]
    public void InvalidRequestsCannotWritePersonalSettings()
    {
        using TestWorld db = new();
        using var session = new Session(db, 1000001);
        session.Select();
        foreach (object?[] args in new object?[][] { [], ["N:x"], ["N:x", null], ["N:x", 7L], ["N:x", "text", 90000002L] })
        {
            Assert.IsType<PilotCallResult.Failure>(session.Call("charMgr", "AddOwnerNote", args));
        }
        Assert.IsType<PilotCallResult.Failure>(session.Call("charMgr", "AddOwnerNote", ["N:x", "text"], new() { ["owner"] = 90000002L }));
        Assert.IsType<PilotCallResult.Failure>(session.Call("charMgr", "GetOwnerNote", [0L]));
        foreach (object?[] args in new object?[][] { [], [null], ["yaml"], [new BlueInstance("evil.Type", null)] })
        {
            Assert.IsType<PilotCallResult.Failure>(session.Call("overviewPresetMgr", "StoreLinkAndGetID", args));
        }
        Assert.IsType<PilotCallResult.Failure>(
            session.Call("sharedSettingsMgr", "StoreSettingLinkAndGetID", [1L, new Dictionary<string, object?>()])
        );
        Assert.Empty(db.Services.GetRequiredService<IPilotNotepad>().List(90000001));
    }

    [Fact]
    public void SettingsWirePreservesNumericNestedKeysAndRejectsExecutableAndUnboundedValues()
    {
        var map = new Dictionary<object, object?> { [0L] = new object?[] { "yes", false, null, 1.5 } };
        string yaml = SharedSettingsWire.Encode(new Dictionary<string, object?> { ["tabs"] = map });
        Assert.Equal("[[\"tabs\", {0: [\"yes\", false, null, 1.5]}]]", yaml);
        foreach (
            object value in new object[]
            {
                double.NaN,
                new BlueInstance("evil", null),
                new object(),
                new string('x', SharedSettingRules.MaximumPayloadBytes + 1),
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => SharedSettingsWire.Encode(new Dictionary<string, object?> { ["bad"] = value }));
        }
        var cycle = new ArrayList();
        cycle.Add(cycle);
        Assert.Throws<InvalidDataException>(() => SharedSettingsWire.Encode(new Dictionary<string, object?> { ["cycle"] = cycle }));
        Assert.Throws<InvalidDataException>(() =>
            SharedSettingsWire.Encode(new Dictionary<string, object?> { ["many"] = new object[20001] })
        );
    }

    private static IDictionary Note(object? value) => Fields(Assert.Single(Assert.IsAssignableFrom<IList>(value).Cast<object>()));

    private static IDictionary Fields(object? value) => Assert.IsAssignableFrom<IDictionary>(Assert.IsType<BlueInstance>(value).State);

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private sealed class Session : IDisposable
    {
        private readonly TestWorld _db;
        private readonly long _accountId;
        private readonly IServiceScope _scope;
        public PilotCallContext Context { get; }

        public Session(TestWorld db, long accountId)
        {
            _db = db;
            _accountId = accountId;
            _scope = db.Services.CreateScope();
            var pilot = db.Store.FindPilot(accountId)!;
            Context = new(
                accountId,
                pilot.Account,
                [pilot],
                p => new(db.Store, p, new TestClock(), new(), db.Objects),
                accountId,
                SignedSettings.Empty
            );
        }

        public void Select() => Context.Select(_db.Store.FindPilot(_accountId)!);

        public PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
