using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.FactionWarfare;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed class MilitiaNavyTests
{
    private const long PilotId = 90000001;
    private const long NeutralId = 90000002;
    private const long AccountId = 1000001;
    private const int HostileSystem = 30000142;

    [Fact]
    public async Task PursuitWaitsAcrossReopenAndDeploysOnlyOneLiveWingPerGrid()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var database = new WorldDatabase(fixture.Path);
        PassiveCatalogFixture.LoadNpcPopulation(database.CatalogPath);
        using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
        {
            const string metadata = "{\"securityStatus\":0.945,\"factionID\":500001}";
            string system = HostileSystem.ToString(System.Globalization.CultureInfo.InvariantCulture);
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems',{system},{metadata})");
        }
        var currentWorld = fixture.Services.GetRequiredService<LiveWorld>();
        using (var action = currentWorld.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new SavedMilitiaEnlistment { PilotId = PilotId, DirectFaction = MilitiaFaction.Gallente });
            action.Commit();
        }
        long ship = fixture.Store.FindPilot(AccountId)!.ShipId;
        fixture.Store.SetPosition(ship, SpacePosition.Zero, HostileSystem);
        var navy = fixture.Services.GetRequiredService<IMilitiaNavy>();
        Assert.Equal(new MilitiaNavyUpdate(MilitiaFaction.Caldari), navy.Advance(PilotId, false, clock.Now));
        Assert.Equal(new MilitiaNavyUpdate(), navy.Advance(NeutralId, true, clock.Now));
        long deadline = clock.Now + TimeSpan.FromSeconds(6).Ticks;
        Assert.Equal(deadline, ReadPursuit(currentWorld)!.RespondAt);
        Assert.Equal(new MilitiaNavyUpdate(), navy.Advance(PilotId, true, deadline - 1));
        await currentWorld.FlushAsync();
        fixture.Services.Dispose();

        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        var live = reopened.GetRequiredService<LiveWorld>();
        var ships = reopened.GetRequiredService<IDockedWorld>();
        navy = reopened.GetRequiredService<IMilitiaNavy>();
        clock.AdvanceWallTime(TimeSpan.FromSeconds(6));
        Assert.Equal(deadline, ReadPursuit(live)!.RespondAt);
        Assert.Equal(new MilitiaNavyUpdate(), navy.Advance(PilotId, false, clock.Now));
        Assert.Empty(Wing(live));

        Assert.Equal(new MilitiaNavyUpdate(SceneChanged: true), navy.Advance(PilotId, true, clock.Now));
        var wing = Wing(live);
        Assert.Equal(new[] { (int)NpcGuardType.CaldariCaptain, (int)NpcGuardType.CaldariMoa }, wing.Select(npc => npc.TypeId));
        Assert.All(wing, npc => Assert.Equal((int)MilitiaFaction.Caldari, npc.FactionId));
        var combat = reopened.GetRequiredService<INpcCombat>();
        Assert.Equal(wing.Select(npc => npc.Id).Order(), combat.Aggressors(ship).Order());
        long neutralShip = Assert.Single(ships.ListPilots(1000002)).ShipId;
        using (var action = live.Economy.Begin(new(ships: [ship, neutralShip])))
        {
            var aggression = action.Set<NpcAggression>().Find(wing[0].Id)!;
            aggression.TargetShipId = neutralShip;
            aggression.LastShotAt = clock.Now;
            aggression.LastTargetId = neutralShip;
            action.Commit();
        }
        Assert.Equal(new MilitiaNavyUpdate(SceneChanged: true), navy.Advance(PilotId, true, clock.Now));
        Assert.Equal(wing.Select(npc => npc.Id).Order(), combat.Aggressors(ship).Order());
        Assert.Empty(combat.Aggressors(neutralShip));
        Assert.Null(live.Economy.Inspect(world => world.Set<NpcAggression>().Find(wing[0].Id)!.LastTargetId));
        using (CatalogDbContext catalog = new(database.CatalogPath))
        {
            var profiles = reopened.GetRequiredService<NpcProfileCatalog>();
            Assert.All(
                wing,
                npc =>
                    Assert.DoesNotContain(
                        profiles.Read(catalog, npc.TypeId).ElectronicWarfare,
                        ability => ability.Kind == NpcEwarKind.WarpDisruption
                    )
            );
        }

        using (var metrics = SessionPerformance.Begin("warmed faction navy checks", _ => { }, force: true)!)
        {
            for (int read = 0; read < 4; read++)
            {
                Assert.Equal(new MilitiaNavyUpdate(), navy.Advance(PilotId, true, clock.Now));
                Assert.Equal(wing.Select(npc => npc.Id), Wing(live).Select(npc => npc.Id));
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }

        clock.AdvanceWallTime(TimeSpan.FromMilliseconds(NpcGuardRules.ReactionMilliseconds));
        var shot = Assert.IsType<NpcShot>(combat.Fire(PilotId, ship, wing[0].Id, SpacePosition.Zero, clock.Now, 0, 1));
        Assert.True(shot.Applied > 0);
        Assert.Null(shot.Capsule);

        Assert.Equal(new MilitiaNavyUpdate(SceneChanged: true), navy.Advance(PilotId, false, clock.Now));
        Assert.Empty(Wing(live));
        Assert.Empty(combat.Aggressors(ship));
        ships.SetPosition(ship, new SpacePosition(2_000_000, 0, 0), HostileSystem);
        clock.AdvanceWallTime(MilitiaNavyRules.RedeployDelay);
        Assert.Equal(new MilitiaNavyUpdate(SceneChanged: true), navy.Advance(PilotId, true, clock.Now));
        Assert.DoesNotContain(Wing(live), replacement => wing.Any(old => old.Id == replacement.Id));

        // Landing back in a station closes the saved pursuit before another dispatch.
        ships.SetPosition(ship, null);
        Assert.Equal(new MilitiaNavyUpdate(SceneChanged: true), navy.Advance(PilotId, false, clock.Now));
        Assert.Null(ReadPursuit(live));
        Assert.Empty(Wing(live));
    }

    [Fact]
    public void NavyTriggersOnlyForOpposingMilitiaShipsInEnemyHighsec()
    {
        Assert.True(MilitiaNavyRules.Responds(MilitiaFaction.Gallente, MilitiaFaction.Caldari, .45, false));
        Assert.False(MilitiaNavyRules.Responds(MilitiaFaction.Gallente, MilitiaFaction.Caldari, .449, false));
        Assert.False(MilitiaNavyRules.Responds(MilitiaFaction.Gallente, MilitiaFaction.Caldari, 1, true));
        Assert.False(MilitiaNavyRules.Responds(MilitiaFaction.Minmatar, MilitiaFaction.Caldari, 1, false));
        Assert.False(MilitiaNavyRules.Responds(null, MilitiaFaction.Caldari, 1, false));
        var notice = new MilitiaNavyWarning(MilitiaFaction.Caldari);
        Assert.Equal("OnRemoteMessage", notice.Name);
        var args = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(notice.Arguments)), 2);
        Assert.Equal("CustomNotify", BlueMarshal.Text(args[0]));
        Assert.Contains("Caldari navy", BlueMarshal.Text(BlueRead.Fields(args[1])["notify"]));
    }

    private static SavedMilitiaNavyPursuit? ReadPursuit(LiveWorld live) =>
        live.Economy.Inspect(world => world.Set<SavedMilitiaNavyPursuit>().Find(PilotId));

    private static WorldNpc[] Wing(LiveWorld live) =>
        live.Economy.Inspect(world =>
            world
                .Filter<WorldNpc>(npc => npc.Role == NpcRole.MilitiaNavy && npc.RetiredAt == null && npc.DestroyedAt == null)
                .OrderBy(npc => npc.SpawnSlot)
                .ToArray()
        );
}
