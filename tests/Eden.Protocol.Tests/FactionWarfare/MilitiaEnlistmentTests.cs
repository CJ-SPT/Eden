using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Repair;
using Eden.Protocol.FactionWarfare;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed class MilitiaEnlistmentTests
{
    private const long First = 90000001;
    private const long Second = 90000002;
    private const int CaldariStation = 60003760;
    private const int GallenteStation = CaldariStation + 1;
    private const int SystemId = 30000142;
    private const int EnemySystem = 30002806;
    private const int EnemyStation = CaldariStation + 2;

    [Fact]
    public async Task DirectEnlistmentRespectsPolicyAndStandingAndMakesOpposingCorpmatesLegalTargets()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        AddMilitiaCatalog(fixture);
        int corporation = CorporationAdministrationTests.CreateAndJoin(fixture);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var militia = fixture.Services.GetRequiredService<IMilitiaEnlistment>();
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Pilots.Find(Second)!.ActiveShip.StationId = GallenteStation;
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => militia.SetAllowedFactions(Second, [MilitiaFaction.Caldari]));
        Assert.Throws<InvalidDataException>(() => militia.EnlistDirectly(First, MilitiaFaction.Caldari));
        Assert.Empty(militia.AllowedFactions(corporation));

        using var scope = fixture.Services.CreateScope();
        var pilot = fixture.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        const string service = "fwCharacterEnlistmentMgr";
        Assert.Same(PilotCallResult.Unsupported, Call("CreateMyDirectEnlistment", [500001L]));
        context.Select(pilot);
        Assert.IsType<PilotCallResult.Failure>(Call("SetMyCorpAllowedEnlistmentFactions", [new List<object?> { 500010L }]));
        Assert.Equal(
            new object?[] { 500001L, 500004L },
            Assert.IsType<List<object?>>(Reply("SetMyCorpAllowedEnlistmentFactions", [new List<object?> { 500004L, 500001L }]))
        );

        SetStanding(-0.01);
        Assert.IsType<PilotCallResult.Failure>(Call("CreateMyDirectEnlistment", [500001L]));
        Assert.Null(militia.Read(First).Effective);
        SetStanding(0);
        Assert.IsType<PilotCallResult.Failure>(Call("CreateMyDirectEnlistment", [500004L]));
        Assert.Null(Reply("CreateMyDirectEnlistment", [500001L]));
        Assert.Equal(MilitiaFaction.Caldari, context.Pilot.Identity.WarFaction);
        Assert.Equal(corporation, context.Pilot.CorpId);
        Assert.Equal(new object?[] { 500001L, 500001L, null }, BlueRead.Tuple(Reply("GetMyEnlistment", []), 3));
        Assert.Equal(
            new object?[] { 500001, 500001, null },
            Assert.Single(context.Notifications.OfType<MilitiaEnlistmentUpdated>()).Arguments
        );

        militia.EnlistDirectly(Second, MilitiaFaction.Gallente);
        Assert.Equal(
            MilitiaFaction.Gallente,
            fixture.Services.GetRequiredService<IPilotCharacterSheet>().ReadPublicProfile(Second)!.MilitiaFaction
        );
        var firstShip = context.Pilot.ShipId;
        var secondShip = fixture.Store.FindPilot(1000002)!.ShipId;
        fixture.Store.SetPosition(firstShip, new(10_000_000, 0, 0));
        fixture.Store.SetPosition(secondShip, new(10_001_000, 0, 0));
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath))
        {
            var killer = action.Pilots.Find(First)!;
            var target = action.Pilots.Find(Second)!;
            var decision = CrimewatchPersistence.CheckPlayer(action, catalog, killer, target, SystemId, clock.Now);
            Assert.Equal(new AggressionDecision(CrimewatchFlag.None, false), decision);
            CrimewatchPersistence.Player(action, catalog, killer.Id, target.Id, SystemId, decision, clock.Now, clock.Now);
            Assert.Null(action.Set<SavedConcordResponse>().Find(firstShip));
            Assert.Null(CrimewatchPersistence.Engagement(action, killer.Id, target.Id));
            Assert.True(CrimewatchPersistence.State(action, killer.Id).Timers().Weapons.Running(clock.Now));
            Assert.True(CrimewatchPersistence.State(action, target.Id).Timers().Pvp.Running(clock.Now));
            Assert.Throws<InvalidDataException>(() => AssistancePersistence.Check(action, catalog, killer, target, clock.Now));
            action.Commit();
        }

        Assert.Null(Reply("RemoveMyDirectEnlistment", []));
        long deadline = clock.Now + TimeSpan.TicksPerDay;
        Assert.Equal(deadline, Reply("GetMyDirectEnlistmentCooldownTimestamp", []));
        Assert.Null(context.Pilot.Identity.WarFaction);
        fixture.Store.SetPosition(firstShip, null);
        clock.AdvanceWallTime(TimeSpan.FromTicks(TimeSpan.TicksPerDay - 1));
        Assert.Throws<InvalidDataException>(() => militia.EnlistDirectly(First, MilitiaFaction.Caldari));
        await live.FlushAsync();
        context.LogOff();
        scope.Dispose();
        fixture.Services.Dispose();

        using var reopened = ServerServices.Build(new(fixture.Path), clock, liveWorld: true);
        militia = reopened.GetRequiredService<IMilitiaEnlistment>();
        Assert.Equal(deadline, militia.Read(First).DirectCooldownUntil);
        Assert.Equal(MilitiaFaction.Gallente, militia.Read(Second).Effective);
        Assert.Equal(new[] { MilitiaFaction.Caldari, MilitiaFaction.Gallente }, militia.AllowedFactions(corporation));
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        militia.EnlistDirectly(First, MilitiaFaction.Caldari);
        Assert.Equal(MilitiaFaction.Caldari, militia.Read(First).Effective);
        Assert.Equal(0L, militia.Read(First).DirectCooldownUntil);

        PilotCallResult Call(string method, object?[] args) =>
            router.Call(context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { });

        object? Reply(string method, object?[] args) =>
            BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(Call(method, args)).Reply));

        void SetStanding(double value)
        {
            using var action = live.Economy.Begin(TransactionParticipants.None);
            var row = action.Set<SavedNpcStanding>().Find(First, (long)MilitiaFaction.Caldari);
            row ??= action.Add(new SavedNpcStanding { PilotId = First, SourceId = (int)MilitiaFaction.Caldari }).Entity;
            row.Value = value;
            action.Commit();
        }
    }

    [Fact]
    public async Task NpcMilitiaUsesNativeJoinAndSelfRetirementWithEmploymentHistory()
    {
        using TestWorld fixture = new(liveWorld: true);
        AddMilitiaCatalog(fixture);
        var original = fixture.Store.FindPilot(1000001)!;
        Queue<object?> input = new(
            new[]
            {
                PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [First]),
                PilotServicesTests.Request("facWarMgr", "JoinFactionAsCharacter", [500001L]),
                PilotServicesTests.Request("fwCharacterEnlistmentMgr", "GetMyEnlistment", []),
                PilotServicesTests.Request("facWarMgr", "GetCorpFactionalWarStatus", []),
                PilotServicesTests.Request("corporationSvc", "GetEmploymentRecord", [First]),
                PilotServicesTests.Request("corpRegistry", "KickOutMember", [First]),
                PilotServicesTests.Request("fwCharacterEnlistmentMgr", "GetMyEnlistment", []),
            }.Select(packet => BlueMarshal.Decode(BlueMarshal.Encode(packet)))
        );
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        List<BlueInstance> output = [];
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
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
        Assert.DoesNotContain(output, packet => packet.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var replies = output.Where(packet => packet.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).ToArray();
        Assert.Equal(7, replies.Length);
        Assert.Equal(new object?[] { 500001L, null, 500001L }, BlueRead.Tuple(Reply(replies[2]), 3));
        var corporationStatus = BlueRead.Fields(Assert.IsType<BlueInstance>(Reply(replies[3])).State);
        Assert.Equal(1L, corporationStatus["status"]);
        Assert.Equal(500001L, corporationStatus["factionID"]);
        var history = BlueRead.Fields(Assert.IsType<BlueInstance>(Reply(replies[4])).State);
        var lines = Assert.IsType<List<object?>>(history["lines"]);
        Assert.Equal((long)MilitiaRules.StateProtectorate, Assert.IsType<List<object?>>(lines[0])[0]);
        Assert.Equal(new object?[] { null, null, null }, BlueRead.Tuple(Reply(replies[6]), 3));

        var affiliationChanges = output
            .Where(packet => packet.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal))
            .Select(packet => BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 3))
            .Select(body => BlueRead.Fields(BlueRead.Tuple(body[1], 2)[1]))
            .Where(fields => fields.ContainsKey("warfactionid"))
            .Select(fields => BlueRead.Tuple(fields["warfactionid"], 2))
            .ToArray();
        Assert.Equal(2, affiliationChanges.Length);
        Assert.Equal(new object?[] { null, 500001L }, affiliationChanges[0]);
        Assert.Equal(new object?[] { 500001L, null }, affiliationChanges[1]);
        var employment = fixture.Services.GetRequiredService<IPlayerCorporations>().Employment(First);
        Assert.Equal(
            new[] { original.CorpId, MilitiaRules.StateProtectorate, original.CorpId },
            employment.Reverse().Select(entry => entry.CorporationId)
        );

        static object? Reply(BlueInstance packet)
        {
            var body = BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 1);
            return BlueMarshal.Decode(BlueRead.Bytes(body[0]));
        }
    }

    [Fact]
    public void NormalDockAndUndockHonorTheCurrentOccupation()
    {
        using TestWorld fixture = new(liveWorld: true);
        AddMilitiaCatalog(fixture);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var militia = fixture.Services.GetRequiredService<IMilitiaEnlistment>();
        var pilot = fixture.Store.FindPilot(1000001)!;
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Pilots.Find(First)!.ActiveShip.StationId = GallenteStation;
            action.Commit();
        }
        militia.JoinNpcMilitia(First, MilitiaFaction.Gallente);
        fixture.Store.SetPosition(pilot.ShipId, pilot.Home.Position);
        fixture.Store.SetPosition(pilot.ShipId, pilot.Home.Position, EnemySystem);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Dock(First, pilot.ShipId, EnemyStation));
        Assert.NotNull(fixture.Store.FindPilot(1000001)!.Position);
        Assert.False(CanUsePublicStructure());

        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new SavedWarzoneOccupation { SystemId = EnemySystem, Occupier = MilitiaFaction.Gallente });
            action.Commit();
        }
        Assert.Null(fixture.Store.Dock(First, pilot.ShipId, EnemyStation).Position);
        Assert.True(CanUsePublicStructure());
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Set<SavedWarzoneOccupation>().Find(EnemySystem)!.Occupier = MilitiaFaction.Caldari;
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => fixture.Store.SetPosition(pilot.ShipId, pilot.Home.Position));
        Assert.Null(fixture.Store.FindPilot(1000001)!.Position);

        bool CanUsePublicStructure()
        {
            using CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath);
            return live.Economy.Inspect(world =>
                Eden.World.Structures.PilotStructureStore.ServiceAccess(
                    world,
                    catalog,
                    new() { SystemId = EnemySystem, PublicAccess = true },
                    First
                )
            );
        }
    }

    [Fact]
    public void EmpireHostilityAndDockingPreserveNeutralAndCapsuleExceptions()
    {
        MilitiaFaction?[] factions = [null, MilitiaFaction.Caldari, MilitiaFaction.Gallente, MilitiaFaction.Amarr, MilitiaFaction.Minmatar];
        foreach (var first in factions)
        {
            foreach (var second in factions)
            {
                bool enemies = first switch
                {
                    MilitiaFaction.Caldari => second == MilitiaFaction.Gallente,
                    MilitiaFaction.Gallente => second == MilitiaFaction.Caldari,
                    MilitiaFaction.Amarr => second == MilitiaFaction.Minmatar,
                    MilitiaFaction.Minmatar => second == MilitiaFaction.Amarr,
                    _ => false,
                };
                Assert.Equal(enemies, MilitiaRules.AreEnemies(first, second));
            }
        }
        var zone = new WarzoneDefinition(WarzoneId.CaldariGallente, MilitiaFaction.Caldari, MilitiaFaction.Gallente, 500010);
        var system = new WarzoneSystem(30002807, zone, MilitiaFaction.Caldari, WarzoneAdjacency.CommandOperations, 0, null);
        Assert.False(MilitiaRules.CanUseDocking(system, MilitiaFaction.Gallente, false));
        Assert.True(MilitiaRules.CanUseDocking(system, MilitiaFaction.Gallente, true));
        Assert.True(MilitiaRules.CanUseDocking(system, MilitiaFaction.Caldari, false));
        Assert.True(MilitiaRules.CanUseDocking(system, null, false));
        Assert.True(MilitiaRules.CanUseDocking(system with { Adjacency = WarzoneAdjacency.Frontline }, MilitiaFaction.Gallente, false));
    }

    private static void AddMilitiaCatalog(TestWorld fixture)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath, writable: true);
        const int gallenteOwner = 1000120;
        catalog.Database.ExecuteSql(
            $"""
            INSERT INTO NpcCorporation(Id,Name) VALUES
                ({MilitiaRules.StateProtectorate},'State Protectorate'),({gallenteOwner},'Gallente Navy'),
                ({MilitiaRules.FederalDefenceUnion},'Federal Defence Union');
            INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
            SELECT {GallenteStation},TypeId,{gallenteOwner},SolarSystemId,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id={CaldariStation};
            INSERT INTO SolarSystem(Id,ConstellationId,Name)
            SELECT {EnemySystem},ConstellationId,'Test warzone system' FROM SolarSystem WHERE Id={SystemId};
            INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
            SELECT {EnemyStation},TypeId,OwnerId,{EnemySystem},OperationId,OrbitId,X,Y,Z FROM Station WHERE Id={CaldariStation};
            """
        );
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES
                ('sde/npcCorporations','1000035','{{"factionID":500001}}'),
                ('sde/npcCorporations','1000120','{{"factionID":500004}}'),
                ('client/systems','30000142','{{"securityStatus":0.945}}'),
                ('client/systems','30002806','{{"securityStatus":0.2}}');
            """
        );
    }
}
