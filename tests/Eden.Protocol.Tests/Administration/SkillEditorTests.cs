using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Server.Administration;
using Eden.Server.Authentication;
using Eden.Server.Launcher;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Administration;

public sealed class SkillEditorTests
{
    [PinnedSkillClientFact]
    public async Task PublishedPackageActivatesOnRestartAndCustomBooksTrainPersistAndRetireSafely()
    {
        string? client = Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT");
        using TestWorld world = new();
        WorldDatabase database = new(world.Path);
        using (CatalogDbContext fixture = new(database.OriginalCatalogPath, writable: true))
        {
            fixture.Database.ExecuteSqlRaw(
                "INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (182,'requiredSkill1'),(277,'requiredSkill1Level')"
            );
            fixture.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (280,'skillLevel'),(20,'speedFactor');
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/marketgroups','1','{{}}'),
                    ('client/stationoperations','14','{{"services":[7]}}'),
                    ('client/dogmaattributes','280','{{"attributeID":280,"name":"skillLevel","defaultValue":0}}'),
                    ('client/dogmaattributes','20','{{"attributeID":20,"name":"speedFactor","defaultValue":0}}'),
                    ('client/dogmaeffects','900001','{{"effectID":900001,"effectName":"fixtureSkillVelocity","effectCategory":0,
                        "modifierInfo":[{{"domain":"itemID","func":"ItemModifier","modifiedAttributeID":20,
                            "modifyingAttributeID":280,"operation":0}},
                            {{"domain":"shipID","func":"ItemModifier","modifiedAttributeID":37,
                                "modifyingAttributeID":20,"operation":6}}]}}');
                """
            );
        }
        SkillEditorStore editor = new(database, TimeProvider.System);
        int prerequisite = editor.Read().Skills.First().Definition.TypeId;
        var custom = editor.Create(
            editor.Read().Generation,
            Draft(editor) with
            {
                Rank = 3,
                BasePrice = 12.34,
                MarketGroupId = 1,
                Prerequisites = [new(prerequisite, 4)],
                AlphaLimits = [new(1, 3), new(2, 0), new(4, 3), new(8, 3)],
                Attributes = [new(20, 5)],
                Effects = [new(900001, true)],
            },
            "Admin"
        );
        SkillClientPublisher publisher = new(database, editor);
        var package = await publisher.Publish(editor.Read().Generation, client!);
        Assert.Null(publisher.Status().Active);
        Assert.Equal(package.Id, publisher.Status().Pending!.Id);
        Assert.DoesNotContain(custom.Definition.TypeId, database.Catalog.Items.Keys);
        WorldDatabase activated = new(world.Path);
        Assert.True(SkillPackageActivation.Activate(activated));
        Assert.Equal(package.Id, publisher.Status().Pending!.Id);
        WorldInitialization.Initialize(activated);
        Assert.Equal(package, publisher.Status().Active);
        Assert.Null(publisher.Status().Pending);
        Assert.Equal(custom.Definition.Name, activated.Catalog.Items[custom.Definition.TypeId].Name);
        await AssertPackageApi(package, publisher, activated, client!);
        const long pilotId = 90000001;
        var pilot = world.Store.FindPilot(1000001)!;
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Set<SavedWorldEconomy>().Single().TestingMarket = false;
            saved.Add(new PilotWallet { PilotId = pilotId, Cents = 1000000 });
            saved.SaveChanges();
        }
        TestClock clock = new();
        var market = new PilotMarketStore(activated, clock, new());
        Assert.Contains(
            market.Orders(pilotId),
            order => order.TypeId == custom.Definition.TypeId && !order.Bid && order.PriceCents == 1234
        );
        long bookId = Assert.Single(market.Buy(pilotId, pilot.StationId, [new(custom.Definition.TypeId, 1234, 1)]).Changes).Item.Id;
        double baselineSpeed = new PilotFittingStore(activated, clock).ShipAttributes(pilotId)[DogmaAttributeId.MaxVelocity];
        var books = new PilotSkillBookStore(activated, clock);
        Assert.Equal(custom.Definition.TypeId, Assert.Single(books.Inject(pilotId, [bookId]).TypeIds));
        var training = new PilotTrainingStore(activated, clock);
        Assert.Throws<InvalidTrainingQueue>(() => training.Save(pilotId, [new(custom.Definition.TypeId, 1)], true));
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Set<PilotSkill>().Find(pilotId, prerequisite)!.Points = TrainedSkill.PointsForLevel(1, 4);
            saved.SaveChanges();
        }
        var queue = training.Save(pilotId, [new(custom.Definition.TypeId, 1)], true);
        Assert.Equal(750, Assert.Single(queue.Queue).EndPoints);
        clock.UtcNow = new DateTimeOffset(DateTime.FromFileTimeUtc(queue.Queue[0].EndTime!.Value));
        Assert.Contains(custom.Definition.TypeId, training.Advance(pilotId).ChangedSkills);
        var learned = new PilotSkillReader(activated).ReadSkills(pilotId).Single(skill => skill.TypeId == custom.Definition.TypeId);
        Assert.Equal(1, learned.Level);
        Assert.Equal(
            baselineSpeed * 1.05,
            new PilotFittingStore(activated, clock).ShipAttributes(pilotId)[DogmaAttributeId.MaxVelocity],
            6
        );
        var reply = Assert.IsType<BlueConstructor>(new TrainedSkillReply(learned).ToBlue());
        Assert.Equal(new object?[] { custom.Definition.TypeId, 1, 750L, 3.0, null }, reply.Arguments);
        editor.Delete(editor.Read().Generation, custom.Definition.TypeId, "Admin");
        var retiredPackage = await publisher.Publish(editor.Read().Generation, client!);
        Assert.Equal(package, publisher.Status().Active);
        WorldDatabase retired = new(world.Path);
        WorldInitialization.Initialize(retired);
        Assert.Equal(retiredPackage, publisher.Status().Active);
        Assert.Equal(0, retired.Catalog.Get(CatalogData.Client.Types, (long)custom.Definition.TypeId).Published);
        Assert.Equal(
            750,
            new PilotSkillReader(retired).ReadSkills(pilotId).Single(skill => skill.TypeId == custom.Definition.TypeId).Points
        );
        Assert.Empty(new PilotTrainingStore(retired, clock).Advance(pilotId).ChangedSkills);
        Assert.Equal(baselineSpeed, new PilotFittingStore(retired, clock).ShipAttributes(pilotId)[DogmaAttributeId.MaxVelocity], 6);
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Set<SavedWorldEconomy>().Single().TestingMarket = false;
            saved.SaveChanges();
        }
        Assert.DoesNotContain(
            new PilotMarketStore(retired, clock, new()).Orders(pilotId),
            order => order.TypeId == custom.Definition.TypeId
        );
    }

    private static async Task AssertPackageApi(
        SkillClientPackage package,
        SkillClientPublisher publisher,
        WorldDatabase database,
        string clientRoot
    )
    {
        int port;
        using (TcpListener listener = new(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        LauncherOptions options = new() { ApiPort = port };
        LauncherStore accounts = new(database, TimeProvider.System);
        accounts.Initialize();
        accounts.Register(new("Skill package pilot", "test password"));
        var session = accounts.SignIn(new("Skill package pilot", "test password", false));
        using var identity = new DevelopmentIdentity(TimeProvider.System, accounts, options);
        await using var api = LauncherApi.Create(options, accounts, identity, TimeProvider.System, skillPublisher: publisher);
        await api.StartAsync();
        using HttpClient http = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/v1/") };
        var advertised = (await http.GetFromJsonAsync<ServerReply>("server"))!;
        Assert.Equal(package, advertised.SkillPackage);
        using var denied = await http.GetAsync($"skill-packages/{package.Id}/code");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        byte[] downloaded = await http.GetByteArrayAsync($"skill-packages/{package.Id}/code");
        Assert.Equal(package.CodeSha256, Convert.ToHexString(SHA256.HashData(downloaded)));
        byte[] prefix = ClientManifest.ReadOriginalPrefix(File.ReadAllBytes(Path.Combine(clientRoot, "tq", "manifest.dat")));
        using var stale = await http.PostAsJsonAsync("launch-tickets", new LaunchRequest(3662168, Convert.ToBase64String(prefix)));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var launched = await http.PostAsJsonAsync(
            "launch-tickets",
            new LaunchRequest(3662168, Convert.ToBase64String(prefix), SkillPackageId: package.Id)
        );
        Assert.Equal(HttpStatusCode.OK, launched.StatusCode);
        var reply = (await launched.Content.ReadFromJsonAsync<LaunchReply>())!;
        Assert.Equal(
            package.CodeSha256,
            Convert.ToHexString(Convert.FromBase64String(reply.Manifest).AsSpan(ClientManifest.CodeHashOffset, SHA256.HashSizeInBytes))
        );
        prefix[100] ^= 1;
        using var changedPrefix = await http.PostAsJsonAsync(
            "launch-tickets",
            new LaunchRequest(3662168, Convert.ToBase64String(prefix), SkillPackageId: package.Id)
        );
        Assert.Equal(HttpStatusCode.BadRequest, changedPrefix.StatusCode);
        accounts.Revoke(session.AccessToken);
        using var revoked = await http.GetAsync($"skill-packages/{package.Id}/code");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        await api.StopAsync();
    }

    private static SkillDefinition Draft(SkillEditorStore store, string name = "Eden skill") =>
        store.Read().Skills.First().Definition with
        {
            TypeId = 0,
            Name = name,
            Published = true,
            IconId = null,
            MarketGroupId = null,
            Prerequisites = [],
            Effects = [],
            Attributes = [],
        };

    [Fact]
    public void EditsRestoreAndCustomRemovalPersistWithoutChangingTheOriginalCatalog()
    {
        using TestWorld world = new();
        WorldDatabase database = new(world.Path);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database.OriginalCatalogPath)));
        SkillEditorStore editor = new(database, TimeProvider.System);
        var initial = editor.Read();
        var original = initial.Skills.First();
        var changed = editor.Save(
            initial.Generation,
            original.Definition with
            {
                Name = "Edited command",
                Rank = 3,
                IconId = null,
                MarketGroupId = null,
            },
            "Admin"
        );
        Assert.False(changed.UserCreated);
        Assert.Equal(original.Definition.Name, changed.Original!.Name);
        Assert.Equal(3, changed.Definition.Rank);
        Assert.Throws<InvalidDataException>(() => editor.Delete(editor.Read().Generation, original.Definition.TypeId, "Admin"));
        SkillEditorStore reopened = new(new(world.Path), TimeProvider.System);
        Assert.Equal(
            "Edited command",
            reopened.Read().Skills.Single(skill => skill.Definition.TypeId == original.Definition.TypeId).Definition.Name
        );
        var restored = reopened.Restore(reopened.Read().Generation, original.Definition.TypeId, null, "Admin");
        Assert.Equal(original.Definition.Name, restored.Definition.Name);
        Assert.Equal(original.Definition.Rank, restored.Definition.Rank);
        Assert.False(restored.IsModified);
        var custom = reopened.Create(reopened.Read().Generation, Draft(reopened), "Admin");
        Assert.True(custom.UserCreated);
        Assert.Null(custom.Original);
        var deleted = reopened.Delete(reopened.Read().Generation, custom.Definition.TypeId, "Admin");
        Assert.True(deleted.Deleted);
        var recovered = reopened.Restore(reopened.Read().Generation, custom.Definition.TypeId, custom.Revision, "Admin");
        Assert.False(recovered.Deleted);
        Assert.True(recovered.UserCreated);
        Assert.Equal(custom.Definition.Name, recovered.Definition.Name);
        var next = reopened.Create(reopened.Read().Generation, Draft(reopened, "Another skill"), "Admin");
        Assert.True(next.Definition.TypeId > custom.Definition.TypeId);
        Assert.Equal(3, reopened.History(custom.Definition.TypeId).Count);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database.OriginalCatalogPath))));
    }

    [Fact]
    public async Task ConcurrentCreatesUseOptimisticGenerationAndProtectStoredOriginsAndHistory()
    {
        using TestWorld world = new();
        using (CatalogDbContext fixture = new(new WorldDatabase(world.Path).OriginalCatalogPath, writable: true))
        {
            fixture.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/dogmaeffects','132','{{"effectID":132,"effectName":"skillEffect","effectCategory":0}}');
                """
            );
        }
        SkillEditorStore editor = new(new(world.Path), TimeProvider.System);
        var initial = editor.Read();
        var draft = Draft(editor);
        var attempts = Enumerable
            .Range(0, 2)
            .Select(index =>
                Task.Run(() =>
                {
                    try
                    {
                        editor.Create(initial.Generation, draft with { Name = $"Skill {index}" }, "Admin");
                        return true;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                })
            );
        Assert.Single(await Task.WhenAll(attempts), created => created);
        var saved = editor.Read();
        Assert.Equal(initial.Generation + 1, saved.Generation);
        var custom = Assert.Single(saved.Skills, skill => skill.UserCreated);
        using SkillEditorDbContext db = new(world.Path);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRaw("UPDATE SkillEdit SET UserCreated=0"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRaw("DELETE FROM SkillEditRevision"));
        Assert.True(editor.Read().Skills.Single(skill => skill.Definition.TypeId == custom.Definition.TypeId).UserCreated);
        Assert.Single(editor.History(custom.Definition.TypeId));
        var original = initial.Skills.First();
        Assert.False(original.CanEditBonuses);
        var protectedBonus = Assert.Throws<InvalidDataException>(() =>
            editor.Save(saved.Generation, original.Definition with { Effects = [new(132, true)] }, "Admin")
        );
        Assert.Contains("dedicated", protectedBonus.Message);
        Assert.Equal(saved.Generation, editor.Read().Generation);
    }

    [Fact]
    public void StaleWritesCyclesAndReferencedDeletionLeaveTheSavedDefinitionsUnchanged()
    {
        using TestWorld world = new();
        SkillEditorStore editor = new(new(world.Path), TimeProvider.System);
        var initial = editor.Read();
        var custom = editor.Create(initial.Generation, Draft(editor), "Admin");
        Assert.Throws<InvalidOperationException>(() => editor.Create(initial.Generation, Draft(editor), "Admin"));
        var dependent = editor.Create(
            editor.Read().Generation,
            Draft(editor, "Dependent") with
            {
                Prerequisites = [new(custom.Definition.TypeId, 3)],
            },
            "Admin"
        );
        long generation = editor.Read().Generation;
        Assert.Throws<InvalidDataException>(() => editor.Delete(generation, custom.Definition.TypeId, "Admin"));
        Assert.Throws<InvalidDataException>(() =>
            editor.Save(generation, custom.Definition with { Prerequisites = [new(dependent.Definition.TypeId, 1)] }, "Admin")
        );
        Assert.Equal(generation, editor.Read().Generation);
        Assert.Empty(editor.Read().Skills.Single(skill => skill.Definition.TypeId == custom.Definition.TypeId).Definition.Prerequisites);
        Assert.Single(editor.History(custom.Definition.TypeId));
        Assert.Throws<InvalidDataException>(() => editor.Restore(generation, custom.Definition.TypeId, dependent.Revision, "Admin"));
    }

    [Theory]
    [InlineData("rank")]
    [InlineData("attribute")]
    [InlineData("duplicate prerequisite")]
    [InlineData("alpha")]
    [InlineData("name")]
    [InlineData("effect")]
    [InlineData("ID")]
    public void InvalidDefinitionsAreRejectedAtomically(string variant)
    {
        using TestWorld world = new();
        SkillEditorStore editor = new(new(world.Path), TimeProvider.System);
        var initial = editor.Read();
        int id = initial.Skills.First().Definition.TypeId;
        var draft = Draft(editor);
        draft = variant switch
        {
            "rank" => draft with { Rank = double.NaN },
            "attribute" => draft with { Attributes = [new((int)DogmaAttributeId.SkillTimeConstant, 2)] },
            "duplicate prerequisite" => draft with { Prerequisites = [new(id, 1), new(id, 2)] },
            "alpha" => draft with { AlphaLimits = [new(1, 6)] },
            "name" => draft with { Name = " " },
            "effect" => draft with { Effects = [new(int.MaxValue, true)] },
            "ID" => draft with { TypeId = id },
            _ => throw new InvalidOperationException(),
        };
        Assert.Throws<InvalidDataException>(() => editor.Create(initial.Generation, draft, "Admin"));
        Assert.Equal(initial.Generation, editor.Read().Generation);
        Assert.DoesNotContain(editor.Read().Skills, skill => skill.UserCreated);
    }

    [Fact]
    public void ProjectedSkillsUseTypedCatalogTrainingAndInjectionDataWhileRetainingTheBaseFingerprint()
    {
        using TestWorld world = new();
        WorldDatabase database = new(world.Path);
        using (CatalogDbContext fixture = new(database.OriginalCatalogPath, writable: true))
        {
            fixture.Database.ExecuteSqlRaw(
                "INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (182,'requiredSkill1'),(277,'requiredSkill1Level')"
            );
        }
        SkillEditorStore editor = new(database, TimeProvider.System);
        var initial = editor.Read();
        int prerequisite = initial.Skills.First().Definition.TypeId;
        var created = editor.Create(
            initial.Generation,
            Draft(editor) with
            {
                Name = "Eden café skill",
                Description = "A custom description.",
                Rank = 3,
                Prerequisites = [new(prerequisite, 2)],
                AlphaLimits = [new(1, 3), new(2, 0), new(4, 1), new(8, 5)],
            },
            "Admin"
        );
        string destination = world.Path + ".projected.sqlite";
        string overlay = SkillCatalogProjection.Create(database.OriginalCatalogPath, destination, editor.Read());
        var projected = CatalogProvider.Open(destination);
        Assert.Equal(initial.CatalogFingerprint, projected.Identity.SourceFingerprint);
        Assert.Equal("Eden café skill", projected.Items[created.Definition.TypeId].Name);
        Assert.Equal(3, projected.Attributes(created.Definition.TypeId)[DogmaAttributeId.SkillTimeConstant]);
        Assert.Equal(2, projected.RequiredSkills(created.Definition.TypeId)[prerequisite]);
        Assert.Equal(1, projected.Get(CatalogData.Client.Types, (long)created.Definition.TypeId).Published);
        Assert.Contains("types.fsdbinary", overlay);
        Assert.Contains("Eden caf", overlay);
        Assert.Equal(
            3,
            projected
                .Get(CatalogData.Client.Clonegrades, "1")
                .Skills!.Value.Single(skill => skill.TypeID == created.Definition.TypeId)
                .Level
        );
    }
}
