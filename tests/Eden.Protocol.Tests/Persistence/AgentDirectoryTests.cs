using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class AgentTestData
{
    internal const long AgentId = 3008416;
    internal const long OtherAgent = 3019370;
    internal const long DeletedAgent = 3008417;
    internal const long NonAgent = 3000001;
    internal const int StationId = 60003760;
    internal const int Corporation = 1000044;

    internal static void Prepare(TestWorld world)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true);
        Add(catalog, AgentId, AgentType.Basic, "Agent Étoile");
        Add(catalog, OtherAgent, AgentType.Career, "School Agent", localization: false);
        Add(catalog, DeletedAgent, AgentType.Basic, "Removed Agent", deleted: true);
        Add(catalog, NonAgent, AgentType.NonAgent, "Public CEO");
        catalog.Add(
            new SourceRecord
            {
                Dataset = "client/npccorporations",
                Key = Corporation.ToString(CultureInfo.InvariantCulture),
                Data = "{\"factionID\":500001}",
            }
        );
        catalog.SaveChanges();
    }

    internal static void Add(
        CatalogDbContext catalog,
        long id,
        AgentType type,
        string name,
        bool deleted = false,
        bool localization = true,
        int? station = StationId,
        int? bloodline = 1
    )
    {
        long nameId = id + 1_000_000;
        catalog.Add(
            new SourceRecord
            {
                Dataset = "client/npccharacters",
                Key = id.ToString(CultureInfo.InvariantCulture),
                Data = JsonSerializer.Serialize(
                    new
                    {
                        deleted = deleted ? 1 : 0,
                        nameID = nameId,
                        corporationID = Corporation,
                        stationID = station,
                        agent = new
                        {
                            agentType = (int)type,
                            agentLevel = 3,
                            agentDivisionID = 22,
                            agentLocateCharacter = 1,
                        },
                        startDate = "2003-05-04 00:32:00",
                        gender = 0,
                        raceID = 1,
                        bloodlineID = bloodline,
                        schoolID = 19,
                        ancestryID = 11,
                        description = "Pinned NPC biography",
                    }
                ),
            }
        );
        catalog.Add(
            new SourceRecord
            {
                Dataset = "sde/npcCharacters",
                Key = id.ToString(CultureInfo.InvariantCulture),
                Data = JsonSerializer.Serialize(new { name = new { en = name } }),
            }
        );
        if (localization)
        {
            catalog.Add(
                new SourceRecord
                {
                    Dataset = "client/localization_fsd_en-us",
                    Key = nameId.ToString(CultureInfo.InvariantCulture),
                    Data = JsonSerializer.Serialize(new { text = name, language = "en-us" }),
                }
            );
        }
    }
}

public sealed class AgentDirectoryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(null)]
    public void AgentHeaderBloodlineComesFromTheCatalogAndSurvivesReopen(int? bloodline)
    {
        using TestWorld db = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            AgentTestData.Add(catalog, AgentTestData.AgentId, AgentType.Basic, "Header Agent", bloodline: bloodline);
            catalog.SaveChanges();
        }
        var directory = db.Services.GetRequiredService<INpcDirectory>();
        Assert.Equal(bloodline, directory.Agent(AgentTestData.AgentId)!.BloodlineId);
        Assert.Equal(directory.Character(AgentTestData.AgentId)!.BloodlineId, directory.Agent(AgentTestData.AgentId)!.BloodlineId);
        using var reopened = ServerServices.Build(new(db.Path));
        Assert.Equal(bloodline, reopened.GetRequiredService<INpcDirectory>().Agent(AgentTestData.AgentId)!.BloodlineId);
    }

    [Fact]
    public void LiveHostBuildWarmsDirectoryBeforePilotRequests()
    {
        using TestWorld db = new();
        AgentTestData.Prepare(db);
        using NameQueryObserver observer = new(new WorldDatabase(db.Path).CatalogPath);
        using var services = ServerServices.Build(new(db.Path), liveWorld: true);
        Assert.Single(observer.ParameterCounts);
        using var metrics = SessionPerformance.Begin("first live directory request", _ => { }, force: true)!;
        var directory = services.GetRequiredService<INpcDirectory>();
        Assert.Equal(2, directory.Agents().Count);
        Assert.Equal("Agent Étoile", directory.Character(AgentTestData.AgentId)!.Identity.Name);
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Single(observer.ParameterCounts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1200)]
    public void ColdDirectoryBindsNameCollectionsOnceRegardlessOfNpcCount(int count)
    {
        using TestWorld db = new();
        string catalogPath = new WorldDatabase(db.Path).CatalogPath;
        using (CatalogDbContext catalog = new(catalogPath, writable: true))
        {
            for (int index = 0; index < count; index++)
            {
                AgentTestData.Add(catalog, AgentTestData.AgentId + index, AgentType.Basic, $"Agent {index}");
            }
            // The directory must only deserialize requested names, even with an empty collection.
            catalog.Add(
                new SourceRecord
                {
                    Dataset = "client/localization_fsd_en-us",
                    Key = "unrelated",
                    Data = "invalid",
                }
            );
            catalog.SaveChanges();
        }
        using NameQueryObserver observer = new(catalogPath);
        var directory = db.Services.GetRequiredService<INpcDirectory>();
        var agents = directory.Agents();
        Assert.Equal(count, agents.Count);
        for (int index = 0; index < count; index++)
        {
            Assert.Equal($"Agent {index}", directory.Character(AgentTestData.AgentId + index)!.Identity.Name);
        }
        if (count == 0)
        {
            Assert.Empty(observer.ParameterCounts);
        }
        else
        {
            Assert.Equal(1, Assert.Single(observer.ParameterCounts));
        }
        Assert.Same(agents, directory.Agents());
        Assert.Equal(count == 0 ? 0 : 1, observer.ParameterCounts.Count);
    }

    private sealed class NameQueryObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _path;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _listeners;
        internal List<int> ParameterCounts { get; } = [];

        internal NameQueryObserver(string path)
        {
            _path = path;
            _listeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore")
            {
                _subscriptions.Add(listener.Subscribe(this));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (
                value.Key == RelationalEventId.CommandExecuting.Name
                && value.Value is CommandEventData data
                && data.Command.Connection?.DataSource == _path
                && (
                    data.Command.CommandText.Contains("client/localization_fsd_en-us", StringComparison.Ordinal)
                    || data.Command.Parameters.Cast<System.Data.Common.DbParameter>()
                        .Any(parameter => Equals(parameter.Value, "client/localization_fsd_en-us"))
                )
            )
            {
                ParameterCounts.Add(
                    data.Command.Parameters.Cast<System.Data.Common.DbParameter>()
                        .Count(parameter => parameter.Value is string text && text.StartsWith('['))
                );
            }
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void Dispose()
        {
            _listeners.Dispose();
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(AgentType.Basic)]
    [InlineData(AgentType.Tutorial)]
    [InlineData(AgentType.Research)]
    [InlineData(AgentType.Concord)]
    [InlineData(AgentType.GenericStoryline)]
    [InlineData(AgentType.Storyline)]
    [InlineData(AgentType.Event)]
    [InlineData(AgentType.FactionalWarfare)]
    [InlineData(AgentType.EpicArc)]
    [InlineData(AgentType.Aura)]
    [InlineData(AgentType.Career)]
    [InlineData(AgentType.Heraldry)]
    public void DirectoryLoadsEveryPinnedAgentCategoryAndPreservesLocationLevelDivisionFactionAndLocator(AgentType type)
    {
        using TestWorld db = new();
        AgentTestData.Prepare(db);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            AgentTestData.Add(catalog, 3020000, type, "Variant");
            catalog.SaveChanges();
        }
        var directory = db.Services.GetRequiredService<INpcDirectory>();
        Assert.Equal(
            new(3020000, AgentTestData.StationId, 30000142, AgentTestData.Corporation, type, 500001, 22, 3, true, 1),
            directory.Agent(3020000)
        );
        Assert.Equal(3, directory.Agents().Count);
        Assert.Null(directory.Agent(AgentTestData.NonAgent));
        Assert.Null(directory.Agent(AgentTestData.DeletedAgent));
        Assert.Null(directory.Character(AgentTestData.DeletedAgent));
        var identity = directory.Character(AgentTestData.AgentId)!.Identity;
        Assert.Equal("Agent Étoile", identity.Name);
        Assert.Equal(AgentTestData.AgentId + 1_000_000, identity.NameId);
        Assert.Equal("School Agent", directory.Character(AgentTestData.OtherAgent)!.Identity.Name);
        Assert.Equal("Public CEO", directory.Character(AgentTestData.NonAgent)!.Identity.Name);
        var names = db
            .Services.GetRequiredService<ICharacterDirectory>()
            .Read([90000001, AgentTestData.AgentId, AgentTestData.NonAgent, -1]);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void SpaceLocationFallbackAndNoFactionPreserveNoneRatherThanInventingStationOrFactionIds()
    {
        using TestWorld db = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            AgentTestData.Add(catalog, AgentTestData.AgentId, AgentType.Event, "Space Agent", station: null);
            catalog.Add(
                new SourceRecord
                {
                    Dataset = "client/agentsinspace",
                    Key = AgentTestData.AgentId.ToString(CultureInfo.InvariantCulture),
                    Data = "{\"solarSystemID\":30000144}",
                }
            );
            catalog.SaveChanges();
        }
        var agent = db.Services.GetRequiredService<INpcDirectory>().Agent(AgentTestData.AgentId)!;
        Assert.Null(agent.StationId);
        Assert.Null(agent.FactionId);
        Assert.Equal(30000144, agent.SystemId);
    }

    [Fact]
    public void FixtureEditsReplaceDirectoryWithoutMutatingSnapshotsAndWorldVersion45AddsPrivateAgentContacts()
    {
        using TestWorld db = new();
        AgentTestData.Prepare(db);
        var directory = db.Services.GetRequiredService<INpcDirectory>();
        var original = directory.Character(AgentTestData.AgentId)!;
        Assert.Equal("Agent Étoile", original.Identity.Name);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            var row = catalog
                .Set<SourceRecord>()
                .AsTracking()
                .Single(row =>
                    row.Dataset == "client/localization_fsd_en-us"
                    && row.Key == (AgentTestData.AgentId + 1_000_000).ToString(CultureInfo.InvariantCulture)
                );
            row.Data = "{\"text\":\"Changed\"}";
            catalog.SaveChanges();
        }
        Assert.Equal("Agent Étoile", original.Identity.Name);
        Assert.Equal("Changed", directory.Character(AgentTestData.AgentId)!.Identity.Name);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldAgentContact; PRAGMA user_version=45");
        }
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        Assert.Equal("Changed", reopened.GetRequiredService<INpcDirectory>().Character(AgentTestData.AgentId)!.Identity.Name);
        Assert.Empty(reopened.GetRequiredService<IPilotContacts>().Read(90000001).Addresses);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(AgentTestData.StationId, saved.Set<SavedHomeStation>().Find(90000001L)!.StationId);
    }

    [Fact]
    public async Task AgentBookmarksArePrivateIdempotentDurableAndDeleteAtomicallyWithPlayerContacts()
    {
        using TestWorld db = new();
        AgentTestData.Prepare(db);
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => contacts.AddAgent(90000001, AgentTestData.AgentId))));
        contacts.Add(90000001, 90000002, 5, true);
        var state = contacts.Read(90000001);
        Assert.Equal(2, state.Addresses.Count);
        Assert.Equal(
            new(AgentTestData.AgentId, 0, false, 0),
            state.Addresses.Single(contact => contact.ContactId == AgentTestData.AgentId)
        );
        Assert.Empty(contacts.Read(90000002).Addresses);
        Assert.Throws<InvalidDataException>(() => contacts.AddAgent(90000001, AgentTestData.NonAgent));
        Assert.Throws<InvalidDataException>(() => contacts.AddAgent(90000001, AgentTestData.DeletedAgent));
        Assert.Throws<InvalidDataException>(() => contacts.AddAgent(90000001, 90000002));
        Assert.Throws<InvalidDataException>(() => contacts.Edit(90000001, AgentTestData.AgentId, 10, true));
        Assert.Throws<InvalidDataException>(() => contacts.Delete(90000001, [AgentTestData.AgentId, 90000002, -1]));
        Assert.Equal(state.Addresses, contacts.Read(90000001).Addresses);
        WorldInitialization.Open(db.Path);
        Assert.Equal(state.Addresses, contacts.Read(90000001).Addresses);
        contacts.Delete(90000001, [AgentTestData.AgentId, 90000002]);
        Assert.Empty(contacts.Read(90000001).Addresses);
        Assert.Equal(2, TaskTrackingTestData.Count(db, TaskEvents.ContactAdded));
    }

    [Fact]
    public void FailedAgentRemovalRollsBackEarlierPlayerContactDeletionInTheSameBatch()
    {
        using TestWorld db = new();
        AgentTestData.Prepare(db);
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        contacts.AddAgent(90000001, AgentTestData.AgentId);
        contacts.Add(90000001, 90000002, 5, true);
        var before = contacts.Read(90000001);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectAgentDelete BEFORE DELETE ON WorldAgentContact BEGIN SELECT RAISE(ABORT, 'forced failure'); END"
            );
        }
        Assert.Throws<SqliteException>(() => contacts.Delete(90000001, [90000002, AgentTestData.AgentId]));
        Assert.Equal(before.Addresses, contacts.Read(90000001).Addresses);
    }

    [Fact]
    public void PlayerOwnerAndProfileLookupsDoNotInitializeTheNpcCatalog()
    {
        using TestWorld db = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            // Deliberately invalid NPC input proves that player-only lookups
            // never enumerate or parse the NPC snapshot in the cold path.
            catalog.Add(
                new SourceRecord
                {
                    Dataset = "client/npccharacters",
                    Key = "3008416",
                    Data = "{}",
                }
            );
            catalog.SaveChanges();
        }
        var directory = db.Services.GetRequiredService<INpcDirectory>();
        Assert.Null(directory.Character(90000001));
        Assert.Null(directory.Agent(90000001));
        Assert.Empty(directory.Characters([90000001, -1]));
        var players = db.Services.GetRequiredService<ICharacterDirectory>().Read([90000001]);
        Assert.Equal("Eden Pilot", Assert.Single(players).Name);
        Assert.Throws<InvalidDataException>(() => directory.Agents());
    }
}
