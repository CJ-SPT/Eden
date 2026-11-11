using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CorporationContactsTests
{
    [Fact]
    public void MembersCannotMutateContactsOrLabelsAndMalformedBatchesLeaveEverythingUnchanged()
    {
        using TestWorld db = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        using CorporationTestClient ceo = new(db, 1000001);
        using CorporationTestClient member = new(db, 1000002);
        ceo.Reply("AddCorporateContact", [Navy, 5L]);
        long label = (long)ceo.Reply("CreateLabel", ["Known", null])!;
        Read(member);
        member.Context.Notifications.Clear();
        var store = db.Services.GetRequiredService<ICorporationContacts>();
        string before = Rows(db);
        long revision = store.Revision(corporation);
        foreach (
            var request in new (string Method, object?[] Args)[]
            {
                ("AddCorporateContact", [Member, 0L]),
                ("EditCorporateContact", [Navy, -5L]),
                ("EditContactsRelationshipID", [new List<object?> { Navy }, 1L]),
                ("RemoveCorporateContacts", [new List<object?> { Navy }]),
                ("CreateLabel", ["Denied", 0L]),
                ("EditLabel", [label, "Denied", null]),
                ("DeleteLabel", [label]),
                ("AssignLabels", [new List<object?> { Navy }, label]),
                ("RemoveLabels", [new List<object?> { Navy }, label]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(member.Call(request.Method, request.Args));
            Assert.Equal(before, Rows(db));
            Assert.Equal(revision, store.Revision(corporation));
            Assert.Empty(member.Context.Notifications);
        }
        foreach (
            var request in new (string Method, object?[] Args)[]
            {
                ("AddCorporateContact", [long.MaxValue, 0L]),
                ("AddCorporateContact", [(long)corporation, 0L]),
                ("AddCorporateContact", [Navy, double.NaN]),
                ("AddCorporateContact", [Navy, 10.1]),
                ("EditContactsRelationshipID", [new List<object?> { Navy, long.MaxValue }, 2L]),
                ("RemoveCorporateContacts", [new List<object?> { Navy, long.MaxValue }]),
                ("RemoveCorporateContacts", [new object?[] { Navy }]),
                ("AssignLabels", [new List<object?> { Navy, Member }, label]),
                ("AssignLabels", [new List<object?> { Navy }, 2L]),
                ("CreateLabel", [" known ", null]),
                ("CreateLabel", ["", null]),
                ("CreateLabel", ["Bad", -1L]),
                ("EditLabel", [label, null, null]),
                ("EditLabel", [3L, "Bad", null]),
                ("DeleteLabel", [2L]),
                ("GetCorporateContacts", [Ceo]),
                ("GetLabels", [corporation]),
            }
        )
        {
            ceo.Context.Notifications.Clear();
            Assert.IsType<PilotCallResult.Failure>(ceo.Call(request.Method, request.Args));
            Assert.Equal(before, Rows(db));
            Assert.Equal(revision, store.Revision(corporation));
            Assert.Empty(ceo.Context.Notifications);
        }
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("GetCorporateContacts", [], new() { ["ownerID"] = corporation }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectorAndTitleDiplomatsUseCurrentRolesAndRevocationTakesEffectImmediately(bool title)
    {
        using TestWorld db = new();
        CorporationAdministrationTests.CreateAndJoin(db);
        var roles = db.Services.GetRequiredService<ICorporationRoles>();
        var store = db.Services.GetRequiredService<ICorporationContacts>();
        if (title)
        {
            roles.UpdateTitles(Ceo, [new(1, "Diplomatic staff", new((long)CorporationRole.Diplomat))]);
            SetRoles(db, 0, 1);
        }
        else
        {
            SetRoles(db, (long)CorporationRole.Director);
        }
        using CorporationTestClient member = new(db, 1000002);
        Assert.Null(member.Reply("AddCorporateContact", [Navy, 10L]));
        if (title)
        {
            roles.DeleteTitle(Ceo, 1);
        }
        else
        {
            SetRoles(db, 0);
        }
        string before = Rows(db);
        Assert.IsType<PilotCallResult.Failure>(member.Call("EditCorporateContact", [Navy, -10L]));
        Assert.Equal(before, Rows(db));
        Assert.Equal(10, Assert.Single(store.Read(Member).Contacts).Standing);
    }

    [Fact]
    public void CorporationContactsAndLabelBitsRemainIndependentAcrossOrganizationsAndPersonalContacts()
    {
        using TestWorld db = new();
        var corporations = db.Services.GetRequiredService<IPlayerCorporations>();
        PlayerCorporationTests.Prepare(db, Ceo);
        PlayerCorporationTests.Prepare(db, Member);
        int first = corporations.Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        int second = corporations.Create(Member, PlayerCorporationTests.Details("Other Explorers", "OTHER")).Corporation.Id;
        var store = db.Services.GetRequiredService<ICorporationContacts>();
        store.SetStanding(Ceo, [second], 5);
        store.SetStanding(Member, [first], -5);
        long firstLabel = store.CreateLabel(Ceo, "Own label", 0);
        long secondLabel = store.CreateLabel(Member, "Other label", null);
        Assert.Equal(firstLabel, secondLabel);
        store.SetLabels(Ceo, [second], firstLabel, true);
        Assert.Equal(firstLabel, Assert.Single(store.Read(Ceo).Contacts).LabelMask);
        Assert.Equal(0, Assert.Single(store.Read(Member).Contacts).LabelMask);
        store.DeleteLabel(Member, secondLabel);
        Assert.Single(store.Read(Ceo).Labels);
        Assert.Empty(store.Read(Member).Labels);
        Assert.Throws<InvalidDataException>(() => store.SetLabels(Member, [first], firstLabel, true));
        Assert.Equal(first, store.Read(Ceo).CorporationId);
        Assert.Equal(second, store.Read(Member).CorporationId);
        Assert.Equal(5, Assert.Single(store.Read(Ceo).Contacts).Standing);
        Assert.Equal(-5, Assert.Single(store.Read(Member).Contacts).Standing);
    }

    [Fact]
    public void CorporationLabelQuotaUsesAllPositiveSignedLongBitsAndDeletingFreesOneBit()
    {
        using TestWorld db = new();
        CorporationAdministrationTests.CreateAndJoin(db);
        var store = db.Services.GetRequiredService<ICorporationContacts>();
        var ids = Enumerable
            .Range(0, Eden.Gameplay.Characters.ContactRules.MaximumLabels)
            .Select(i => store.CreateLabel(Ceo, "Label " + i, null))
            .ToArray();
        Assert.Equal(1L << 62, ids[^1]);
        Assert.Throws<InvalidDataException>(() => store.CreateLabel(Ceo, "Overflow", 0));
        store.SetStanding(Ceo, [Member], 1);
        store.SetLabels(Ceo, [Member], long.MaxValue, true);
        Assert.Equal(long.MaxValue, Assert.Single(store.Read(Ceo).Contacts).LabelMask);
        store.DeleteLabel(Ceo, ids[^1]);
        Assert.Equal(long.MaxValue & ~ids[^1], Assert.Single(store.Read(Ceo).Contacts).LabelMask);
        Assert.Equal(ids[^1], store.CreateLabel(Ceo, "Replacement", 0));
    }

    private static string Rows(TestWorld db)
    {
        using WorldDbContext world = new(db.Path);
        return JsonSerializer.Serialize(
            new
            {
                Contacts = world.Set<SavedCorporationContact>().OrderBy(c => c.CorporationId).ThenBy(c => c.ContactId).ToArray(),
                Labels = world.Set<SavedCorporationContactLabel>().OrderBy(l => l.CorporationId).ThenBy(l => l.Id).ToArray(),
            }
        );
    }
}
