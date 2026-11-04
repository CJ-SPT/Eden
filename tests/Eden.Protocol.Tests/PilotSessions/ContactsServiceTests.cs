using Eden.Gameplay.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class ContactsServiceTests
{
    [Fact]
    public void NativeContactAndLabelCallsUpdateTypedStateAndResolveOfflineOwners()
    {
        using Fixture fixture = new();
        Assert.IsType<PilotCallResult.UnsupportedCall>(fixture.Call("GetContactList", []));
        fixture.Select();
        Assert.Empty(Assert.IsType<List<object?>>(Fields(Reply(fixture.Call("GetContactList", [])))["addresses"]));
        Assert.Null(Reply(fixture.Call("AddContact", [90000002L, 5L, true, false, null])));
        Assert.Null(Reply(fixture.Call("EditContact", [90000002L, -5d, false, false, ""])));
        Assert.Null(Reply(fixture.Call("EditContactsRelationshipID", [new List<object?> { 90000002L }, 10L])));
        long label = Assert.IsType<long>(Reply(fixture.Call("CreateLabel", [new BlueUnicode("星間旅行"), null])));
        Assert.Null(Reply(fixture.Call("AssignLabels", [new List<object?> { 90000002L }, label])));
        Assert.Null(Reply(fixture.Call("BlockOwners", [new List<object?> { 90000002L }])));
        var book = Fields(Reply(fixture.Call("GetContactList", [])));
        var address = Fields(Assert.Single(Assert.IsType<List<object?>>(book["addresses"])));
        Assert.Equal(90000002L, address["contactID"]);
        Assert.Equal(10d, address["relationshipID"]);
        Assert.Equal(false, address["inWatchlist"]);
        Assert.Equal(label, address["labelMask"]);
        Assert.Equal(90000002L, Fields(Assert.Single(Assert.IsType<List<object?>>(book["blocked"])))["senderID"]);
        var labels = Assert.IsType<Dictionary<long, object?>>(Reply(fixture.Call("GetLabels", [])));
        Assert.Equal("星間旅行", Fields(labels[label])["name"]);
        Assert.Null(Fields(labels[label])["color"]);
        Assert.Null(Reply(fixture.Call("EditLabel", [label, "Fleet", 3L])));
        Assert.Null(Reply(fixture.Call("RemoveLabels", [new List<object?> { 90000002L }, label])));
        Assert.Null(Reply(fixture.Call("AssignLabels", [new List<object?> { 90000002L }, label])));
        Assert.Null(Reply(fixture.Call("DeleteLabel", [label])));
        Assert.Equal(
            0L,
            Fields(Assert.Single(Assert.IsType<List<object?>>(Fields(Reply(fixture.Call("GetContactList", [])))["addresses"])))["labelMask"]
        );
        Assert.Empty(Fields(Reply(fixture.Call("GetLabels", []))));
        var owner = BlueRead.Tuple(
            Reply(
                fixture.Router.Call(
                    fixture.Context,
                    "config",
                    "GetMultiOwnersEx",
                    new object?[] { new List<object?> { 90000002L } },
                    _ => { }
                )
            ),
            2
        );
        var row = BlueRead.Tuple(Assert.Single(Assert.IsType<List<object?>>(owner[1])), 5);
        Assert.Equal(90000002L, row[0]);
        Assert.Equal("Eden Wing", BlueMarshal.Text(row[1]));
        Assert.Null(Reply(fixture.Call("UnblockOwners", [new List<object?> { 90000002L }])));
        Assert.Null(Reply(fixture.Call("DeleteContacts", [new List<object?> { 90000002L }])));
        Assert.Empty(fixture.Contacts.Read(90000001).Addresses);
        Assert.Empty(fixture.Contacts.Read(90000001).Blocked);
        Assert.Empty(fixture.Context.Notifications);
    }

    [Fact]
    public void InvalidAndUnimplementedContactRequestsFailWithoutChangingEitherPilot()
    {
        using Fixture fixture = new();
        fixture.Select();
        Reply(fixture.Call("AddContact", [90000002L, 5L, false, false, null]));
        foreach (
            (string method, object?[] args) in new (string, object?[])[]
            {
                ("AddContact", [90000001L, 0L, false, false, null]),
                ("AddContact", [123L]),
                ("EditContact", [90000002L, 0L, false, true, "Notify"]),
                ("EditContact", [90000002L, double.NaN, false, false, null]),
                ("EditContact", [90000002L, 0L, false, false, 7L]),
                ("DeleteContacts", [90000002L]),
                ("DeleteContacts", [new List<object?> { 90000002L, 123L }]),
                ("BlockOwners", [new List<object?> { 90000002L, "invalid" }]),
                ("UnblockOwners", [new List<object?>()]),
                ("AssignLabels", [new List<object?> { 90000002L }, 1L]),
                ("CreateLabel", ["", 0L]),
                ("CreateLabel", ["Bad color", -1L]),
                ("GetContactList", [90000002L]),
                ("GetLabels", [90000002L]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call(method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(
            fixture.Call("DeleteContacts", [new List<object?> { 90000002L }], new() { ["pilotId"] = 90000002L })
        );
        Assert.Equal(new PersonalContact(90000002, 5, false, 0), Assert.Single(fixture.Contacts.Read(90000001).Addresses));
        Assert.Empty(fixture.Contacts.Read(90000001).Blocked);
        Assert.Empty(fixture.Contacts.Read(90000002).Addresses);
        Assert.Empty(fixture.Contacts.Labels(90000001));
        Assert.Empty(fixture.Context.Notifications);
    }

    private static Dictionary<string, object?> Fields(object? value) =>
        BlueRead.Fields(value is BlueInstance instance ? instance.State : value);

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public IServiceScope Scope { get; }
        public PilotCallContext Context { get; }
        public PilotCallRouter Router => Scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        public IPilotContacts Contacts => Db.Services.GetRequiredService<IPilotContacts>();

        public Fixture()
        {
            Scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(
                1000001,
                pilot.Account,
                [pilot],
                p => new(Db.Store, p, TimeProvider.System, new(), Db.Objects),
                42,
                SignedSettings.Empty
            );
        }

        public void Select() => Context.Select(Db.Store.FindPilot(1000001)!);

        public PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            Router.Call(Context, "charMgr", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            Scope.Dispose();
            Db.Dispose();
        }
    }
}
