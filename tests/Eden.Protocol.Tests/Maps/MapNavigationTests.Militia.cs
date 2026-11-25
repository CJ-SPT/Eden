using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Maps;

public sealed partial class MapNavigationTests
{
    [Fact]
    public async Task MilitiaKillHistoryUsesCommittedVictimsAndSurvivesCheckpointReopen()
    {
        const long killer = 90000001;
        const long victim = 90000002;
        const int location = 30000142;
        const int ibis = 601;
        const long ordinaryKills = 3;
        const long militiaKills = 5;
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock);
        WorldDatabase database = new(fixture.Path);
        using (CatalogDbContext edit = new(database.CatalogPath, writable: true))
        {
            edit.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (29,'Capsule',6),(186,'Wreck',2);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (670,'Capsule',29),(26510,'Rookie Wreck',186);
                UPDATE SourceRecord SET Data=json_set(Data,'$.wreckTypeID',26510) WHERE Dataset='client/types' AND Key='601';
                """
            );
        }
        using var host = ServerServices.Build(database, clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        var ships = host.GetRequiredService<SqliteWorld>();
        var at = new SpacePosition(100_000, 0, 0);
        ships.SetPosition(ships.FindPilot(1000002)!.ShipId, at, location);

        DestroyPilot(MilitiaFaction.Caldari, commit: false);
        Assert.Empty(Read(host, militiaKills, 1).Lines);
        DestroyPilot(MilitiaFaction.Caldari, commit: true);
        DestroyNpc(NpcRole.MilitiaNavy, NpcAnchorKind.Gate);
        DestroyNpc(NpcRole.MilitiaComplexDefender, NpcAnchorKind.MilitiaComplex);
        object?[] militiaRow = [(long)location, 3L, 2L, 0L];
        var (version, lines) = Read(host, militiaKills, 1);
        Assert.Equal(militiaRow, Assert.IsType<object?[]>(Assert.Single(lines)));

        clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        DestroyPilot(null, commit: true);
        Assert.Equal(version, Read(host, militiaKills, 1, measure: true).Version);
        Assert.Equal(militiaRow, Assert.IsType<object?[]>(Assert.Single(Read(host, militiaKills, 24).Lines)));
        Assert.Equal(
            new object?[] { (long)location, 3L, 1L, 0L },
            Assert.IsType<object?[]>(Assert.Single(Read(host, ordinaryKills, 1).Lines))
        );
        await live.FlushAsync();
        host.Dispose();
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        Assert.Equal(militiaRow, Assert.IsType<object?[]>(Assert.Single(Read(reopened, militiaKills, 1).Lines)));
        clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        Assert.Empty(Read(reopened, militiaKills, 1).Lines);
        Assert.Equal(militiaRow, Assert.IsType<object?[]>(Assert.Single(Read(reopened, militiaKills, 24).Lines)));
        clock.AdvanceWallTime(TimeSpan.FromHours(23));
        Assert.Empty(Read(reopened, militiaKills, 24).Lines);

        void DestroyPilot(MilitiaFaction? faction, bool commit)
        {
            using var action = live.Economy.Begin(new(systems: [location], pilots: [killer, victim]));
            var attacker = action.Pilots.Find(killer)!;
            var defender = action.Pilots.Find(victim)!;
            var affiliation =
                action.Set<SavedMilitiaEnlistment>().Find(victim) ?? action.Add(new SavedMilitiaEnlistment { PilotId = victim }).Entity;
            affiliation.DirectFaction = faction;
            var ship = defender.ActiveShip;
            ship.TypeId = ibis;
            using CatalogDbContext catalog = new(database.CatalogPath);
            ShipLossPersistence.Apply(
                action,
                catalog,
                defender,
                null,
                ship.Id,
                location,
                ship.Position!,
                clock.Now,
                host.GetRequiredService<IShipLootRoll>(),
                PilotInteractionHistoryStore.Player(action, attacker, attacker.ActiveShipId)
            );
            if (commit)
            {
                action.Commit();
            }
        }

        void DestroyNpc(NpcRole role, NpcAnchorKind anchor)
        {
            using var action = live.Economy.Begin(new(systems: [location], pilots: [killer]));
            using CatalogDbContext catalog = new(database.CatalogPath);
            var attacker = action.Pilots.Find(killer)!;
            var npc = action
                .Add(
                    new WorldNpc
                    {
                        SystemId = location,
                        TypeId = ibis,
                        CorporationId = MilitiaRules.Corporation(MilitiaFaction.Caldari),
                        FactionId = (int)MilitiaFaction.Caldari,
                        Role = role,
                        AnchorKind = anchor,
                        X = at.X,
                        UpdatedAt = clock.Now,
                    }
                )
                .Entity;
            var result = NpcDamagePersistence.Apply(
                action,
                catalog,
                npc,
                attacker,
                attacker.ActiveShipId,
                new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.StructureHitPoints] = 10 },
                new(100, 0, 0, 0),
                clock.Now,
                bypassResistances: true
            );
            Assert.True(result.Destroyed);
            action.Commit();
        }

        (object?[] Version, List<object?> Lines) Read(IServiceProvider services, long statistic, int hours, bool measure = false)
        {
            using var scope = services.CreateScope();
            var store = services.GetRequiredService<SqliteWorld>();
            var pilot = store.FindPilot(1000001)!;
            using PilotCallContext context = new(
                1000001,
                pilot.Account,
                [pilot],
                p => new(store, p, clock, new(), []),
                42,
                SignedSettings.Empty
            );
            context.Select(pilot);
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            using var metrics = measure ? SessionPerformance.Begin("militia map kills", _ => { }, force: true) : null;
            var reply = Assert.IsType<PilotCallResult.Success>(
                router.Call(context, "map", "GetHistory", new object?[] { statistic, (long)hours }, _ => { })
            );
            if (metrics is not null)
            {
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            return ReadHistory(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply)));
        }
    }
}
