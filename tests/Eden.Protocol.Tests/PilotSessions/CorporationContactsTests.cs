using System.Text.Json;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CorporationContactsTests
{
    private const long Ceo = 90000001;
    private const long Member = 90000002;
    private const long Navy = 1000035;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCorporationContactsAndLabelsSupportDiplomatEditsAndPersistAcrossReopen(bool liveWorld)
    {
        using TestWorld db = new(liveWorld: liveWorld);
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        using CorporationTestClient ceo = new(db, 1000001);
        using CorporationTestClient member = new(db, 1000002);
        var store = db.Services.GetRequiredService<ICorporationContacts>();
        Assert.Empty(Read(ceo));
        Assert.Null(ceo.Reply("AddCorporateContact", [Member, 2.5]));
        var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(Read(member)[Member]).State);
        Assert.Equal(Member, fields["contactID"]);
        Assert.Equal(2.5, fields["relationshipID"]);
        Assert.Equal(false, fields["inWatchlist"]);
        Assert.Equal(0L, fields["labelMask"]);
        long label = Assert.IsType<long>(ceo.Reply("CreateLabel", [new BlueUnicode("Trusted pilots"), 0L]));
        var labels = Assert.IsType<Dictionary<long, object?>>(ceo.Reply("GetLabels", []));
        var labelFields = BlueRead.Fields(Assert.IsType<BlueInstance>(labels[label]).State);
        Assert.Equal(label, labelFields["labelID"]);
        Assert.Equal("Trusted pilots", labelFields["name"]);
        Assert.Equal(0L, labelFields["color"]);
        Assert.Null(ceo.Reply("AssignLabels", [new List<object?> { Member }, label]));
        Assert.Equal(label, Assert.Single(store.Read(Member).Contacts).LabelMask);
        Assert.Null(ceo.Reply("EditCorporateContact", [Member, -5L]));
        Assert.Equal(label, Assert.Single(store.Read(Member).Contacts).LabelMask);
        SetRoles(db, (long)CorporationRole.Diplomat);
        Assert.Null(member.Reply("AddCorporateContact", [Navy, 10L]));
        Assert.Null(member.Reply("EditContactsRelationshipID", [new List<object?> { Member, Navy }, 5L]));
        Assert.All(store.Read(Ceo).Contacts, c => Assert.Equal(5, c.Standing));
        Assert.Null(member.Reply("AssignLabels", [new List<object?> { Navy }, label]));
        Assert.Null(member.Reply("RemoveLabels", [new List<object?> { Member }, label]));
        Assert.Equal(0, Assert.Single(store.Read(Ceo).Contacts, c => c.ContactId == Member).LabelMask);
        Assert.Null(member.Reply("EditLabel", [label, "Allies", null]));
        Assert.Equal(new(label, "Allies", 0), Assert.Single(store.Read(Member).Labels));
        Assert.Null(member.Reply("EditLabel", [label, null, 7L]));
        Assert.Equal(7, Assert.Single(store.Read(Member).Labels).Color);
        Assert.Null(member.Reply("RemoveCorporateContacts", [new List<object?> { Member }]));
        Assert.Null(member.Reply("DeleteLabel", [label]));
        var book = store.Read(Ceo);
        Assert.Equal(new(Navy, 5, 0), Assert.Single(book.Contacts));
        Assert.Empty(book.Labels);
        Assert.Empty(db.Services.GetRequiredService<IPilotContacts>().Read(Ceo).Addresses);
        Assert.Empty(db.Services.GetRequiredService<IPilotContacts>().Read(Member).Addresses);
        string before = JsonSerializer.Serialize(book);
        ceo.Dispose();
        member.Dispose();
        db.Services.Dispose();
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var final = reopened.GetRequiredService<ICorporationContacts>().Read(Member);
        Assert.Equal(corporation, final.CorporationId);
        Assert.Equal(before, JsonSerializer.Serialize(final));
    }

    private static Dictionary<long, object?> Read(CorporationTestClient client)
    {
        var reply = client.Reply("GetCorporateContacts", []);
        return reply is Dictionary<string, object?> { Count: 0 } ? [] : Assert.IsType<Dictionary<long, object?>>(reply);
    }

    private static void SetRoles(TestWorld db, long mask, long titleMask = 0) =>
        db
            .Services.GetRequiredService<ICorporationRoles>()
            .UpdateMembers(Ceo, [new(Member, null, null, null, new(mask, 0, 0, 0, 0, 0, 0, 0), null, titleMask)]);
}
