using Eden.Data.World.Corporations;
using Eden.Data.World.Fittings;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Fittings;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Corporations;
using Eden.Protocol.Fittings;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Fittings;

public sealed partial class SavedFittingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorporationMembersReadTemplatesAndFittingManagersMutateWithNativeReplies(bool liveWorld)
    {
        using TestWorld db = new(liveWorld: liveWorld);
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        using Client ceo = new(db, service: "corpFittingMgr");
        using Client member = new(db, Other, service: "corpFittingMgr");
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        long id = Assert.IsType<long>(ceo.Success("SaveFitting", [(long)corporation, NativePlan(owner: corporation)]));
        Assert.Single(store.Read(Other, corporation));
        string before = SavedRows(db);
        foreach (
            var request in new (string Method, object?[] Args)[]
            {
                ("SaveFitting", [(long)corporation, NativePlan(owner: corporation)]),
                ("UpdateFitting", [(long)corporation, NativePlan(owner: corporation), id]),
                ("SaveManyFittings", [(long)corporation, Batch(corporation)]),
                ("DeleteFitting", [(long)corporation, id]),
                ("DeleteManyFittings", [(long)corporation, new List<object?> { id }]),
                ("UpdateNameAndDescription", [id, (long)corporation, "Changed", ""]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(member.Call(request.Method, request.Args));
            Assert.Equal(before, SavedRows(db));
            Assert.Empty(member.Context.Notifications);
        }
        SetFittingRoles(db, (long)CorporationRole.FittingManager);
        Assert.Equal(
            id,
            member.Success(
                "UpdateFitting",
                [(long)corporation, NativePlan(name: "Updated", owner: corporation)],
                new() { ["fittingID"] = id }
            )
        );
        var changed = Assert.IsType<FittingsUpdatedNotification>(Assert.Single(member.Context.Notifications));
        Assert.Equal(new long[] { id }, changed.FittingIds);
        member.Context.Notifications.Clear();
        Assert.Null(member.Success("UpdateNameAndDescription", [id, (long)corporation, "Renamed", "Corporation plan"]));
        var mappings = Assert.IsType<List<object?>>(member.Success("SaveManyFittings", [(long)corporation, Batch(corporation)]));
        Assert.Equal(2, mappings.Count);
        Assert.Equal(3, store.Read(Owner, corporation).Count);
        Assert.Empty(store.Read(Owner));
        Assert.Empty(store.Read(Other));
        Assert.Null(member.Success("DeleteFitting", [(long)corporation, id]));
        Assert.Contains(member.Context.Notifications, n => n is FittingDeletedNotification { FittingId: var deleted } && deleted == id);
        var remaining = store.Read(Other, corporation).Select(f => (object?)f.Id).ToList();
        Assert.Equal(2, Assert.IsType<List<object?>>(member.Success("DeleteManyFittings", [(long)corporation, remaining])).Count);
        Assert.Empty(store.Read(Other, corporation));
        ceo.Dispose();
        member.Dispose();
        db.Services.Dispose();
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        Assert.Empty(reopened.GetRequiredService<IPilotSavedFittings>().Read(Other, corporation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectorAndTitleRolesAuthorizeButRevokedOrForeignMembershipRejects(bool title)
    {
        using TestWorld db = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        var roles = db.Services.GetRequiredService<ICorporationRoles>();
        if (title)
        {
            roles.UpdateTitles(Owner, [new(1, "Fitting staff", new((long)CorporationRole.FittingManager))]);
            SetFittingRoles(db, 0, titleMask: 1);
        }
        else
        {
            SetFittingRoles(db, (long)CorporationRole.Director);
        }
        long id = store.Save(Other, Plan("Authorized"), ownerId: corporation);
        if (title)
        {
            roles.DeleteTitle(Owner, 1);
        }
        else
        {
            SetFittingRoles(db, 0);
        }
        string before = SavedRows(db);
        Assert.Throws<InvalidDataException>(() => store.Rename(Other, id, "Revoked", "", corporation));
        Assert.Equal(before, SavedRows(db));
        SetFittingRoles(db, (long)CorporationRole.FittingManager);
        using (WorldDbContext world = new(db.Path))
        {
            world.Pilots.Find(Other)!.CorporationId = 1000166;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => store.Read(Other, corporation));
        Assert.Throws<InvalidDataException>(() => store.Save(Other, Plan("Foreign"), ownerId: corporation));
        Assert.Throws<InvalidDataException>(() => store.DeleteMany(Other, corporation, [id]));
        Assert.Equal(before, SavedRows(db));
        Assert.Single(store.Read(Owner, corporation));
        Assert.Empty(store.Read(Other, 1000166));
        Assert.Throws<InvalidDataException>(() => store.Save(Other, Plan("NPC"), ownerId: 1000166));
    }

    [Fact]
    public void CorporationQuotaIsSixHundredAndCannotBypassPersonalOwnership()
    {
        using TestWorld db = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        var imports = Enumerable
            .Range(1, SavedFittingRules.MaximumCorporationFittings)
            .Select(i => new FittingImport(-i, Plan("Fit " + i)))
            .ToArray();
        var ids = store.SaveMany(Owner, corporation, imports);
        Assert.Equal(600, ids.Count);
        Assert.Throws<InvalidDataException>(() => store.Save(Owner, Plan("Overflow"), ownerId: corporation));
        long id = ids[0].FittingId;
        Assert.Equal(id, store.Save(Owner, Plan("Updated"), id, corporation));
        Assert.Throws<InvalidDataException>(() => store.Save(Owner, Plan("Steal"), id));
        Assert.Throws<InvalidDataException>(() => store.Delete(Owner, id));
        Assert.Throws<InvalidDataException>(() => store.Read(Other, Owner));
        store.DeleteMany(Owner, corporation, [id]);
        Assert.Single(store.SaveMany(Owner, corporation, [new(-1, Plan("Replacement"))]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimedSessionsReceiveCommittedAddsUpdatesAndDeletesWithoutUnchangedDatabasePolling(bool corporationOwner)
    {
        using TestWorld db = new();
        long owner = corporationOwner ? CorporationAdministrationTests.CreateAndJoin(db) : Owner;
        string service = corporationOwner ? "corpFittingMgr" : "charFittingMgr";
        using Client actor = new(db, service: service);
        using Client observer = new(db, corporationOwner ? Other : Owner, service: service);
        observer.Success("GetFittings", [owner]);
        long id = Assert.IsType<long>(actor.Success("SaveFitting", [owner, NativePlan(owner: owner)]));
        observer.AdvanceFittings();
        Assert.Equal(id, Assert.IsType<FittingAddedNotification>(Assert.Single(observer.Context.Notifications)).FittingId);
        observer.Context.Notifications.Clear();
        actor.Success("UpdateFitting", [owner, NativePlan(name: "Changed", owner: owner), id]);
        observer.AdvanceFittings();
        var update = Assert.IsType<FittingsUpdatedNotification>(Assert.Single(observer.Context.Notifications));
        Assert.Equal(new long[] { id }, update.FittingIds);
        observer.Context.Notifications.Clear();
        actor.Success("UpdateNameAndDescription", [id, owner, "Renamed", "Changed description"]);
        observer.AdvanceFittings();
        Assert.IsType<FittingsUpdatedNotification>(Assert.Single(observer.Context.Notifications));
        observer.Context.Notifications.Clear();
        using (var measure = SessionPerformance.Begin("warm fitting monitor", _ => { }, force: true)!)
        {
            for (int i = 0; i < 20; i++)
            {
                observer.AdvanceFittings();
            }
            Assert.Equal(0, measure.Commands);
            Assert.Equal(0, measure.Connections);
            Assert.Equal(0, measure.FittingCalculations);
        }
        Assert.Empty(observer.Context.Notifications);
        actor.Success("DeleteFitting", [owner, id]);
        observer.AdvanceFittings();
        Assert.Equal(
            new long[] { id },
            Assert.IsType<FittingsDeletedNotification>(Assert.Single(observer.Context.Notifications)).FittingIds
        );
        observer.Context.Notifications.Clear();
        observer.AdvanceFittings();
        Assert.Empty(observer.Context.Notifications);
    }

    [Fact]
    public void RemovedMemberDoesNotReceiveFittingChangesEvenWithAStaleSessionIdentity()
    {
        using TestWorld db = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        using Client member = new(db, Other, service: "corpFittingMgr");
        member.Success("GetFittings", [(long)corporation]);
        using (WorldDbContext world = new(db.Path))
        {
            world.Pilots.Find(Other)!.CorporationId = 1000166;
            world.SaveChanges();
        }
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        store.Save(Owner, Plan("Hidden"), ownerId: corporation);
        member.AdvanceFittings();
        Assert.Empty(member.Context.Notifications);
        Assert.IsType<PilotCallResult.Failure>(member.Call("GetFittings", [(long)corporation]));
    }

    [Fact]
    public void LocalDeleteAcknowledgementDoesNotAbsorbOtherCommittedFittingChanges()
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        long first = store.Save(Owner, Plan("First"));
        long second = store.Save(Owner, Plan("Second"));
        long third = store.Save(Owner, Plan("Third"));
        using Client client = new(db);
        client.List();
        // Other requests commit after this session's prior update and before its
        // delete acknowledgement captures a new baseline.
        store.Rename(Owner, second, "Changed concurrently", "");
        store.Delete(Owner, third);
        long fourth = store.Save(Owner, Plan("Added concurrently"));
        store.Delete(Owner, first);
        client.AcknowledgeFittings(Owner, first);
        Assert.Equal(fourth, Assert.Single(client.Context.Notifications.OfType<FittingAddedNotification>()).FittingId);
        Assert.Equal(new long[] { third }, Assert.Single(client.Context.Notifications.OfType<FittingsDeletedNotification>()).FittingIds);
        Assert.Equal(new long[] { second }, Assert.Single(client.Context.Notifications.OfType<FittingsUpdatedNotification>()).FittingIds);
        client.Context.Notifications.Clear();
        client.AdvanceFittings();
        Assert.Empty(client.Context.Notifications);
    }

    private static void SetFittingRoles(TestWorld db, long mask, long titleMask = 0) =>
        db
            .Services.GetRequiredService<ICorporationRoles>()
            .UpdateMembers(Owner, [new(Other, null, null, null, new(mask, 0, 0, 0, 0, 0, 0, 0), null, titleMask)]);
}
