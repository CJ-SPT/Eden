using System.Text.Json;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Fittings;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Fittings;

public sealed class SavedFittingsTests
{
    private const long Owner = 90000001;
    private const long Other = 90000002;
    private const int BrutixNavyIssue = 33151;

    [Fact]
    public async Task NativeSessionRepliesBeforeNotifyingTheFittingCacheAndRequiresSelection()
    {
        using TestWorld db = new();
        Queue<object?> input = new(
            new[]
            {
                PilotServicesTests.Request("charFittingMgr", "SaveFitting", [Owner, NativePlan()]),
                PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [Owner]),
                PilotServicesTests.Request("charFittingMgr", "SaveFitting", [Owner, NativePlan()]),
            }
        );
        List<BlueInstance> output = [];
        using var scope = db.Services.CreateScope();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            scope
                .ServiceProvider.GetRequiredService<IPilotSession>()
                .RunAsync(
                    () => input.TryDequeue(out var packet) ? Task.FromResult(packet) : throw new EndOfStreamException(),
                    packet =>
                    {
                        output.Add(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(packet))));
                        return Task.CompletedTask;
                    },
                    _ => { },
                    1000001,
                    42,
                    SignedSettings.Empty,
                    CancellationToken.None
                )
        );
        Assert.EndsWith(".ErrorResponse", output[0].Name);
        var notification = Assert.Single(
            output,
            p =>
            {
                var destination = Assert.IsType<BlueInstance>(BlueRead.Tuple(p.State, 14)[2]);
                return p.Name.EndsWith(".Notification", StringComparison.Ordinal)
                    && BlueRead.Tuple(destination.State, 4)[1] is byte[] name
                    && BlueMarshal.Text(name) == "OnFittingAdded";
            }
        );
        int index = output.IndexOf(notification);
        Assert.EndsWith(".CallRsp", output[index - 1].Name);
        var response = BlueRead.Tuple(output[index - 1].State, 14);
        long fittingId = Assert.IsType<long>(BlueMarshal.Decode(BlueRead.Bytes(BlueRead.Tuple(response[4], 1)[0])));
        Assert.Equal(Assert.Single(db.Services.GetRequiredService<IPilotSavedFittings>().Read(Owner)).Id, fittingId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSaveReloadOverwriteRenameAndDeleteKeepAssetsUnchanged(bool inSpace)
    {
        using TestWorld db = new(emptyWallets: false);
        PassiveCatalogFixture.LoadCombatHulls(new WorldDatabase(db.Path).CatalogPath, new HashSet<int> { BrutixNavyIssue });
        using Client client = new(db, inSpace: inSpace);
        string assets = Assets(db);
        Assert.Empty(client.List());
        long id = Assert.IsType<long>(client.Success("SaveFitting", [Owner, NativePlan(BrutixNavyIssue)]));
        Assert.True(id > 0);
        var notice = Assert.IsType<FittingAddedNotification>(Assert.Single(client.Context.Notifications));
        Assert.Equal(new object?[] { Owner, id }, notice.Arguments);
        var fit = Assert.IsType<BlueInstance>(client.List()[id]);
        var fields = BlueRead.Fields(fit.State);
        Assert.Equal("utillib.KeyVal", fit.Name);
        Assert.Equal(id, fields["fittingID"]);
        Assert.Equal(Owner, fields["ownerID"]);
        Assert.Equal((long)BrutixNavyIssue, fields["shipTypeID"]);
        Assert.Equal("Brutix 星 🚀", fields["name"]);
        Assert.Equal("<b>Saved plan</b>\nDescription", fields["description"]);
        Assert.True(Assert.IsType<long>(fields["savedDate"]) > 0);
        var entries = Assert.IsType<List<object?>>(fields["fitData"]);
        Assert.Equal(new object?[] { 3651L, 27L, 1L }, entries[0]);
        Assert.Equal(new object?[] { 3651L, 5L, 23L }, entries[1]);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var store = reopened.GetRequiredService<IPilotSavedFittings>();
        Assert.Equal("Brutix 星 🚀", Assert.Single(store.Read(Owner)).Plan.Name);
        Assert.Empty(store.Read(Other));
        Assert.Equal(id, client.Success("UpdateFitting", [Owner, NativePlan(601, "Updated")], new() { ["fittingID"] = id }));
        Assert.Single(client.List());
        Assert.Null(client.Success("UpdateNameAndDescription", [id, Owner, "Renamed", "New description"]));
        WorldInitialization.Open(db.Path);
        var saved = Assert.Single(store.Read(Owner));
        Assert.Equal(id, saved.Id);
        Assert.Equal(601, saved.Plan.ShipTypeId);
        Assert.Equal("Renamed", saved.Plan.Name);
        Assert.Equal("New description", saved.Plan.Description);
        Assert.Equal(assets, Assets(db));
        Assert.Null(client.Success("DeleteFitting", [Owner, id]));
        WorldInitialization.Open(db.Path);
        Assert.Empty(store.Read(Owner));
        Assert.Equal(assets, Assets(db));
    }

    [Fact]
    public void OwnerAndFittingIdsCannotReadChangeOrDeleteAnotherPilotsPlans()
    {
        using TestWorld db = new();
        using Client first = new(db);
        using Client second = new(db, Other);
        long id = Assert.IsType<long>(first.Success("SaveFitting", [Owner, NativePlan()]));
        string saved = SavedRows(db);
        string assets = Assets(db);
        foreach (
            var request in new (string Method, object?[] Args, Dictionary<string, object?>? Keywords)[]
            {
                ("GetFittings", [Owner], null),
                ("SaveFitting", [Owner, NativePlan()], null),
                ("SaveFitting", [Other, NativePlan(owner: Owner)], null),
                ("UpdateFitting", [Other, NativePlan(owner: Other)], new() { ["fittingID"] = id }),
                ("DeleteFitting", [Other, id], null),
                ("UpdateNameAndDescription", [id, Other, "Stolen", ""], null),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(second.Call(request.Method, request.Args, request.Keywords));
            Assert.Equal(saved, SavedRows(db));
            Assert.Equal(assets, Assets(db));
            Assert.Empty(second.Context.Notifications);
        }
        Assert.Empty(second.List());
        Assert.Single(first.List());
    }

    [Fact]
    public void MalformedNativePlansAndRequestsFailWithoutSavingOrEmittingNotifications()
    {
        using TestWorld db = new();
        using Client client = new(db);
        string assets = Assets(db);
        var valid = NativePlan();
        object?[] invalidPlans =
        [
            null,
            valid with
            {
                Name = "untrusted.Class",
            },
            Change("name", " "),
            Change("name", new string('x', SavedFittingRules.MaximumNameLength + 1)),
            Change("description", new string('x', SavedFittingRules.MaximumDescriptionLength + 1)),
            Change("name", "Invalid@@name"),
            Change("description", "Null\0text"),
            Change("shipTypeID", 3651L),
            Change("shipTypeID", long.MaxValue),
            Change("shipTypeID", 999999L),
            Change("fitData", new List<object?>()),
            Change("fitData", new object?[] { new object?[] { 3651L, 27L, 1L } }),
            Change(
                "fitData",
                new List<object?>
                {
                    new List<object?> { 3651L, 27L, 1L },
                }
            ),
            Change("fitData", new List<object?> { new object?[] { 3651L, 27L, 0L } }),
            Change("fitData", new List<object?> { new object?[] { 3651L, 27L, -1L } }),
            Change("fitData", new List<object?> { new object?[] { 3651L, 27L, long.MaxValue } }),
            Change("fitData", new List<object?> { new object?[] { 3651L, 4L, 1L } }),
            Change("fitData", new List<object?> { new object?[] { 999999L, 27L, 1L } }),
            Change("fitData", new List<object?> { new object?[] { true, 27L, 1L } }),
            Change("fitData", Enumerable.Repeat<object?>(new object?[] { 3651L, 5L, 1L }, SavedFittingRules.MaximumItems + 1).ToList()),
        ];
        foreach (var plan in invalidPlans)
        {
            Assert.IsType<PilotCallResult.Failure>(client.Call("SaveFitting", [Owner, plan]));
            Assert.Empty(client.List());
            Assert.Empty(client.Context.Notifications);
            Assert.Equal(assets, Assets(db));
        }
        Assert.IsType<PilotCallResult.Failure>(client.Call("SaveFitting", [Owner, valid], new() { ["unexpected"] = true }));
        Assert.IsType<PilotCallResult.Failure>(client.Call("UpdateFitting", [Owner, valid], new() { ["fittingID"] = long.MaxValue }));
        Assert.IsType<PilotCallResult.Failure>(client.Call("DeleteFitting", [Owner, 0L]));
        Assert.Empty(client.List());

        BlueInstance Change(string field, object? value)
        {
            var fields = new Dictionary<string, object?>(BlueRead.Fields(valid.State)) { [field] = value };
            return valid with { State = fields };
        }
    }

    [Fact]
    public void NativeTextLimitsAndDatabaseRollbackPreserveTheSavedPlan()
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        FittingPlan maximum = new(
            new string('x', SavedFittingRules.MaximumNameLength),
            new string('y', SavedFittingRules.MaximumDescriptionLength),
            601,
            [new(3651, InventoryFlag.HighSlot0, 1)]
        );
        long id = store.Save(Owner, maximum);
        string before = SavedRows(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER FailSavedFittingUpdate BEFORE UPDATE ON WorldSavedFitting
                BEGIN SELECT RAISE(ABORT,'forced fitting failure'); END;
                """
            );
        }
        Assert.Throws<DbUpdateException>(() => store.Save(Owner, maximum with { Name = "Changed" }, id));
        Assert.Equal(before, SavedRows(db));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER FailSavedFittingUpdate");
        }
        WorldInitialization.Open(db.Path);
        Assert.Equal(maximum.Name, Assert.Single(store.Read(Owner)).Plan.Name);
    }

    [Fact]
    public void QuotaAppliesToCreatesWhileUpdatesAndDeletingFreeCapacityRemainAvailable()
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        using (WorldDbContext world = new(db.Path))
        {
            world.AddRange(
                Enumerable
                    .Range(0, SavedFittingRules.MaximumFittings)
                    .Select(i => new SavedFittingRecord
                    {
                        OwnerId = Owner,
                        ShipTypeId = 601,
                        Name = "Fit " + i,
                        ItemsJson = "[]",
                    })
            );
            world.SaveChanges();
        }
        FittingPlan plan = new("Maximum", "", 601, [new(3651, InventoryFlag.HighSlot0, 1)]);
        string before = SavedRows(db);
        Assert.Throws<InvalidDataException>(() => store.Save(Owner, plan));
        Assert.Equal(before, SavedRows(db));
        long id = store.Read(Owner)[0].Id;
        Assert.Equal(id, store.Save(Owner, plan, id));
        store.Delete(Owner, id);
        Assert.True(store.Save(Owner, plan) > id);
        Assert.Equal(SavedFittingRules.MaximumFittings, store.Read(Owner).Count);
        Assert.True(store.Save(Other, plan) > 0);
    }

    [Theory]
    [InlineData(82)]
    [InlineData(83)]
    public void UpgradePreservesThePilotAndAssetsAndReopensSavedPlans(int version)
    {
        using TestWorld db = new();
        string assets = Assets(db);
        var pilot = db.Store.FindPilot(1000001);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldSavedFitting");
            world.Database.ExecuteSqlRaw(version == 82 ? "PRAGMA user_version=82" : "PRAGMA user_version=83");
        }
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        FittingPlan plan = new("Migrated", "", 601, [new(3651, InventoryFlag.HighSlot0, 1)]);
        long id = store.Save(Owner, plan);
        WorldInitialization.Open(db.Path);
        Assert.Equal(id, Assert.Single(store.Read(Owner)).Id);
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
        Assert.Equal(assets, Assets(db));
        using WorldDbContext final = new(db.Path);
        Assert.Equal(84, final.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData(27, true)]
    [InlineData(11, true)]
    [InlineData(19, true)]
    [InlineData(92, true)]
    [InlineData(94, true)]
    [InlineData(95, false)]
    [InlineData(125, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    [InlineData(164, true)]
    [InlineData(171, true)]
    [InlineData(156, true)]
    [InlineData(87, true)]
    [InlineData(158, true)]
    [InlineData(5, true)]
    [InlineData(4, false)]
    public void TemplateLocationsMatchPinnedNativeFittingFlags(int flag, bool allowed)
    {
        FittingPlan plan = new("Template", "", 601, [new(3651, (InventoryFlag)flag, 1)]);
        if (allowed)
        {
            SavedFittingRules.Check(plan);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => SavedFittingRules.Check(plan));
        }
    }

    private static BlueInstance NativePlan(int hull = 601, string name = "Brutix 星 🚀", long owner = Owner) =>
        new(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["ownerID"] = owner,
                ["name"] = new BlueUnicode(name),
                ["description"] = new BlueUnicode("<b>Saved plan</b>\nDescription"),
                ["shipTypeID"] = (long)hull,
                ["fitData"] = new List<object?> { new object?[] { 3651L, 27L, 1L }, new object?[] { 3651L, 5L, 23L } },
            }
        );

    private static string SavedRows(TestWorld db)
    {
        using WorldDbContext world = new(db.Path);
        return JsonSerializer.Serialize(world.Set<SavedFittingRecord>().OrderBy(f => f.Id).ToArray());
    }

    private static string Assets(TestWorld db)
    {
        using WorldDbContext world = new(db.Path);
        return JsonSerializer.Serialize(
            new
            {
                Ships = world.Ships.OrderBy(s => s.Id).ToArray(),
                Items = world.Set<OwnedItem>().OrderBy(i => i.Id).ToArray(),
                Wallet = world.Database.SqlQueryRaw<long>("SELECT Cents AS Value FROM WorldWallet ORDER BY PilotId").ToArray(),
            }
        );
    }

    private sealed class Client : IDisposable
    {
        private readonly IServiceScope _scope;
        private readonly long _owner;
        public PilotCallContext Context { get; }

        public Client(TestWorld db, long owner = Owner, bool inSpace = false)
        {
            _scope = db.Services.CreateScope();
            _owner = owner;
            var pilot = db.Store.FindPilot(owner == Owner ? 1000001 : 1000002)!;
            if (inSpace)
            {
                db.Store.SetPosition(pilot.ShipId, new(1_000_000, 0, 0));
                pilot = db.Store.FindPilot(1000001)!;
            }
            Context = new(
                owner == Owner ? 1000001 : 1000002,
                pilot.Account,
                [pilot],
                _scope.ServiceProvider.GetRequiredService<Eden.Protocol.Flight.FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            );
            Context.Select(pilot);
        }

        public PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, "charFittingMgr", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public object? Success(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(Call(method, args, keywords)).Reply));

        public Dictionary<long, object?> List()
        {
            var reply = Success("GetFittings", [_owner]);
            if (reply is Dictionary<string, object?> { Count: 0 })
            {
                return [];
            }
            return Assert.IsType<Dictionary<long, object?>>(reply);
        }

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
