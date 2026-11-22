using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerMilitiaPatrolTests
{
    [Fact]
    public void FullSafetyPilotSeesLocksAndTradesLegalFireWithOpposingMilitiaPatrol()
    {
        const long account = 1000001;
        const int lockTimeout = 20;
        const int combatTimeout = 30;
        const string firingEffect = "projectileFired";
        var directory = TestDirectory.Create("militia-patrol-player-combat-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            TestClock clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
            PrepareCatalog(database.CatalogPath);
            Configure(database, clock);
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            var live = host.GetRequiredService<LiveWorld>();
            StagePilots(live, database, clock, includeCaldari: false);
            var ships = host.GetRequiredService<SqliteWorld>();
            long humanShip = ships.FindPilot(account)!.ShipId;
            long gun = FitPlayerShip(live, database, humanShip, clock);
            var enlistment = host.GetRequiredService<IMilitiaEnlistment>();
            enlistment.JoinNpcMilitia(Human, MilitiaFaction.Caldari);
            host.GetRequiredService<IPilotSafety>().Set(Human, ShipSafetyLevel.Full);

            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            var concord = host.GetRequiredService<ConcordMaintenance>();
            var presence = host.GetRequiredService<IHumanPresence>();
            using var observer = presence.Select(Human, Jita);
            scheduler.PopulationPrepared(Aldranette);
            HaulerObservation? patrol = null;
            for (int second = 0; second < MaximumSeconds; second++)
            {
                Tick();
                patrol = scheduler.Hauler(GallentePilot);
                if (patrol?.Work.MilitiaPatrol is { Phase: MilitiaPatrolPhase.Holding })
                {
                    break;
                }
            }
            Assert.Equal(MilitiaPatrolPhase.Holding, patrol?.Work.MilitiaPatrol?.Phase);
            Assert.Equal(MilitiaFaction.Gallente, enlistment.Read(GallentePilot).Effective);
            var enemyPosition = Assert.IsType<SpacePosition>(patrol!.Pilot.Position);

            // Keep the fixture's human grid entry separate from the flight, target and combat paths under test.
            ships.SetPosition(humanShip, enemyPosition + new SpacePosition(1_000, 0, 0), Aldranette);
            var human = ships.FindPilot(account)!;
            using PilotCallContext client = new(
                account,
                human.Account,
                [human],
                host.GetRequiredService<FlightSessionFactory>().Create,
                account,
                SignedSettings.Empty
            );
            client.Select(human);
            observer.Dispose();
            Tick();
            client.Flight.Snapshot();
            Assert.Equal(MilitiaFaction.Caldari, client.Pilot.Identity.WarFaction);
            var snapshot = client.Flight.Updates.Select(update => update.Command).OfType<DestinyCommand.SetState>().Last().Snapshot;
            var enemy = Assert.Single(snapshot.Slims.OfType<ShipSlimItem>(), slim => slim.Ship.Pilot.ShipId == GallenteShip);
            var fields = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(enemy.ToBlue()).State);
            Assert.Equal((int)MilitiaFaction.Gallente, fields["warFactionID"]);
            Assert.NotNull(client.Flight.FindTarget(GallenteShip));

            using var scope = host.CreateScope();
            var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
            var combat = scope.ServiceProvider.GetRequiredService<CombatHandler>();
            List<PilotDamage> shots = [];
            host.GetRequiredService<PilotDamageSignal>().Applied += damage =>
            {
                if (damage.Applied > 0 && (damage.PilotId, damage.AttackerId) is (Human, GallentePilot) or (GallentePilot, Human))
                {
                    shots.Add(damage);
                }
            };
            Assert.IsType<PilotCallResult.Failure>(combat.Call(client, "dogmaIM", "Activate", [gun, firingEffect, GallenteShip, 1L]));
            Assert.IsNotType<PilotCallResult.Failure>(targeting.Call(client, "dogmaIM", "AddTarget", [GallenteShip]));
            for (int second = 0; second < lockTimeout && !targeting.IsLocked(GallenteShip); second++)
            {
                Tick();
                targeting.Advance(client);
            }
            Assert.True(targeting.IsLocked(GallenteShip));
            Assert.Same(PilotCallResult.None, combat.Call(client, "dogmaIM", "Activate", [gun, firingEffect, GallenteShip, 10L]));
            for (int second = 0; second < combatTimeout && shots.Select(shot => shot.AttackerId).Distinct().Count() < 2; second++)
            {
                Tick();
                targeting.Advance(client);
                combat.Advance(client);
            }
            Assert.Contains(shots, shot => shot.AttackerId == Human && shot.WeaponType == Railgun);
            Assert.Contains(shots, shot => shot.AttackerId == GallentePilot && shot.WeaponType == Railgun);

            var crimewatch = host.GetRequiredService<IPilotCrimewatch>();
            var status = host.GetRequiredService<IPilotSecurityStatus>();
            Assert.Equal(ShipSafetyLevel.Full, crimewatch.Read(Human, Aldranette).Safety);
            foreach (long pilot in new[] { Human, GallentePilot })
            {
                var timers = crimewatch.Read(pilot, Aldranette).Timers;
                Assert.Equal(CrimewatchFlag.None, timers.Flag(clock.Now));
                Assert.True(timers.Weapons.Running(clock.Now));
                Assert.DoesNotContain(status.History(pilot), entry => entry.Event == SecurityEvent.IllegalAggression);
            }

            void Tick()
            {
                clock.Seconds++;
                maintenance.Tick();
                concord.Tick();
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static long FitPlayerShip(LiveWorld live, WorldDatabase database, long ship, TestClock clock)
    {
        using var world = live.Economy.Begin(new(pilots: [Human], ships: [ship]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var hull = world.Ships.Find(ship)!;
        hull.TypeId = Merlin;
        hull.StationId = NennamailaStation;
        TrafficFitting.Apply(
            world,
            catalog,
            Human,
            ship,
            TrafficSnapshotTests.Profile() with
            {
                HullTypeId = Merlin,
                Equipment = [new(Railgun, (int)InventoryFlag.HighSlot0, 1), new(Antimatter, (int)InventoryFlag.HighSlot0, 50)],
            },
            clock.Now
        );
        long gun = Assert.Single(world.FittedItems(Human, ship), item => item.TypeId == Railgun).Id;
        world.Commit();
        return gun;
    }
}
