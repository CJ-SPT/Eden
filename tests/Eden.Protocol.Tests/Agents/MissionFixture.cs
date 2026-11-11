using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

internal sealed class MissionFixture : IDisposable
{
    internal const long PilotId = 90000001;
    internal const long AgentId = AgentTestData.AgentId;
    internal const long StorylineId = 3019999;
    internal const int StationId = 60003760;
    internal const int DestinationId = 60003761;
    internal TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero));
    internal TestWorld Db { get; }
    private ServiceProvider? _live;
    private IServiceScope? _scope;
    private PilotCallContext? _context;
    private readonly bool _combatScene;
    internal IServiceProvider Services => _live ?? Db.Services;
    internal IPilotMissions Missions => Services.GetRequiredService<IPilotMissions>();
    internal PilotCallContext Context => _context ??= CreateContext();

    internal MissionFixture(
        int level = 1,
        int division = MissionRules.DistributionDivision,
        bool live = false,
        Action<CatalogDbContext>? configure = null,
        bool combatScene = false
    )
    {
        _combatScene = combatScene;
        Db = new(clock: Clock);
        string path = new WorldDatabase(Db.Path).CatalogPath;
        PassiveCatalogFixture.LoadMissions(path);
        AgentTestData.Prepare(Db);
        using (CatalogDbContext catalog = new(path, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60003761,TypeId,OwnerId,30000144,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                """
            );
            Agent(catalog, AgentId, AgentType.Basic, level, division);
            Agent(catalog, StorylineId, AgentType.GenericStoryline, 1, MissionRules.DistributionDivision);
            configure?.Invoke(catalog);
        }
        using (WorldDbContext world = new(Db.Path))
        {
            world.Pilots.Include(p => p.Account).Single(p => p.Id == PilotId).Account.CloneGrade = AccountCloneGrade.Omega;
            world.SaveChanges();
        }
        if (live)
        {
            _live = ServerServices.Build(new(Db.Path), Clock, liveWorld: true);
        }
    }

    internal static void Agent(CatalogDbContext catalog, long id, AgentType type, int level, int division)
    {
        string key = id.ToString(CultureInfo.InvariantCulture);
        string raw = JsonSerializer.Serialize(
            new
            {
                deleted = 0,
                nameID = id + 1_000_000,
                corporationID = AgentTestData.Corporation,
                stationID = StationId,
                agent = new
                {
                    agentType = (int)type,
                    agentLevel = level,
                    agentDivisionID = division,
                    agentLocateCharacter = 0,
                },
            }
        );
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/npccharacters',{key},{raw})");
        string name = JsonSerializer.Serialize(new { name = new { en = $"Mission Agent {id}" } });
        catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO SourceRecord VALUES ('sde/npcCharacters',{key},{name})");
    }

    internal void Dock(int station)
    {
        using WorldDbContext world = new(Db.Path);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == PilotId);
        if (pilot.ActiveShip.Position is { } position)
        {
            world.Remove(position);
        }
        pilot.ActiveShip.StationId = station;
        world.SaveChanges();
        if (_context is not null)
        {
            _context.ChangeShip(Db.Store.FindPilot(1000001)!);
        }
    }

    internal MissionOffer Request(long agent = AgentId) => Missions.Act(PilotId, agent, MissionAction.Request).Offer!;

    internal MissionOffer Accept(MissionOffer offer) => Missions.Act(PilotId, offer.Agent.Id, MissionAction.Accept, offer.Id).Offer!;

    internal void Kill(long npcId, bool commit = true)
    {
        if (Eden.World.Runtime.LiveWorld.Find(Db.Path) is { } live)
        {
            using var action = live.Economy.Begin(new(pilots: [PilotId], resources: [new(SharedSimulationResource.Npc, npcId)]));
            using var residentCatalog = new CatalogDbContext(new WorldDatabase(Db.Path).CatalogPath);
            var residentNpc = action.Set<WorldNpc>().Find(npcId)!;
            var residentPilot = action.Pilots.Single(p => p.Id == PilotId);
            var residentProfile = new NpcProfileCatalog().Read(residentCatalog, residentNpc.TypeId);
            var residentResult = NpcDamagePersistence.Apply(
                action,
                residentCatalog,
                residentNpc,
                residentPilot,
                residentPilot.ActiveShipId,
                residentProfile.Attributes,
                new DamageVector(1_000_000, 1_000_000, 1_000_000, 1_000_000),
                Clock.Now
            );
            Assert.True(residentResult.Destroyed);
            if (commit)
            {
                action.Commit();
            }
            return;
        }
        using WorldDbContext world = new(Db.Path);
        using CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath);
        using var transaction = world.Database.BeginTransaction();
        var npc = world.Set<WorldNpc>().Find(npcId)!;
        var pilot = world.Pilots.Single(p => p.Id == PilotId);
        var profile = new NpcProfileCatalog().Read(catalog, npc.TypeId);
        var result = NpcDamagePersistence.Apply(
            world,
            catalog,
            npc,
            pilot,
            pilot.ActiveShipId,
            profile.Attributes,
            new DamageVector(1_000_000, 1_000_000, 1_000_000, 1_000_000),
            Clock.Now
        );
        Assert.True(result.Destroyed);
        world.SaveChanges();
        if (commit)
        {
            transaction.Commit();
        }
        else
        {
            transaction.Rollback();
        }
    }

    private PilotCallContext CreateContext()
    {
        _scope = Services.CreateScope();
        var pilot = Db.Store.FindPilot(1000001)!;
        var context = new PilotCallContext(
            1000001,
            pilot.Account,
            [pilot],
            p => new FlightSession(
                Db.Store,
                p,
                Clock,
                new(),
                Db.Objects,
                missions: Missions,
                encounters: _combatScene ? Services.GetRequiredService<INpcEncounters>() : null
            ),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        return context;
    }

    internal PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        var context = Context;
        return _scope!
            .ServiceProvider.GetRequiredService<PilotCallRouter>()
            .Call(context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
    }

    internal T Handler<T>()
        where T : notnull
    {
        _ = Context;
        return _scope!.ServiceProvider.GetRequiredService<T>();
    }

    internal static object? Reply(PilotCallResult reply) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(reply).Reply));

    internal void StopResident()
    {
        _context?.Dispose();
        _context = null;
        _scope?.Dispose();
        _scope = null;
        _live?.Dispose();
        _live = null;
    }

    public void Dispose()
    {
        StopResident();
        Db.Dispose();
    }
}
