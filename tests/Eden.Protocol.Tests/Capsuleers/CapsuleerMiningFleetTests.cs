using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Protocol.Flight;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerMiningFleetTests
{
    private const int HomeSystem = 30000141;
    private const int Veldspar = 1230;

    [Fact]
    public async Task ExistingMinerGainsStripMiningCrewThatSharesABeltAndRespondsTogetherAcrossRestart()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        PassiveCatalogFixture.LoadMining(fixture.Database.CatalogPath);
        PassiveCatalogFixture.LoadTechTwoHulls(fixture.Database.CatalogPath);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z)
                    SELECT Id+1,SolarSystemId,TypeId,GroupId,X+1000000000,Y,Z FROM Celestial WHERE Id=40009051;
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z)
                    SELECT Id+2,SolarSystemId,TypeId,GroupId,X+2000000000,Y,Z FROM Celestial WHERE Id=40009051;
                INSERT INTO SourceRecord VALUES('client/asteroidcountbytypeidbeltid','40009052','{{"1230":12}}');
                INSERT INTO SourceRecord VALUES('client/asteroidcountbytypeidbeltid','40009053','{{"1230":12}}');
                """
            );
        }
        MinerObservation[] saved;
        var observer = Guid.NewGuid();
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            using (WorldActionScope setup = new(fixture.Database, new(systems: [HomeSystem])))
            using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
            {
                const long seed = 1;
                setup.World.Add(new SavedCapsuleerProvisioning { Id = 1, WorldSeed = seed });
                setup.World.Add(
                    new SavedCapsuleerSystem
                    {
                        SystemId = HomeSystem,
                        PolicyVersion = CapsuleerProvisioningRules.Version,
                        CatalogFingerprint = CapsuleerProvisioner.Fingerprint(
                            fixture.Database,
                            catalog,
                            seed,
                            setup.World.Settings.Capsuleers
                        ),
                    }
                );
                setup.Commit();
            }
            var identities = host.GetRequiredService<ICapsuleerCommunity>();
            long founder = Assert.Single(identities.Pilots(), pilot => pilot.Career == CapsuleerCareer.Mining).PilotId;
            host.GetRequiredService<IHumanPresence>().Update(observer, HomeSystem);
            host.PrepareActivatedPopulations();
            long[] crew = identities
                .Pilots()
                .Where(pilot => pilot.Career == CapsuleerCareer.Mining)
                .Select(pilot => pilot.PilotId)
                .Order()
                .ToArray();
            Assert.Equal(CapsuleerMiningRules.MaximumCrew, crew.Length);
            Assert.Contains(founder, crew);
            var onGrid = Until(host, fixture.Clock, crew, miners => miners.All(miner => miner.Work.Activity == MinerActivity.Mining));
            Assert.Single(onGrid.Select(miner => miner.Work.WorkingBelt).Distinct());
            Assert.Equal(onGrid.Length, onGrid.Select(miner => miner.Work.TargetId).Distinct().Count());
            Assert.Equal((int)CapsuleerMiningHull.Venture, onGrid.Single(miner => miner.Pilot.PilotId == founder).Pilot.ShipTypeId);
            Assert.Contains(
                onGrid,
                miner =>
                    (CapsuleerMiningHull)miner.Pilot.ShipTypeId
                        is CapsuleerMiningHull.Covetor
                            or CapsuleerMiningHull.Retriever
                            or CapsuleerMiningHull.Procurer
            );
            Assert.Contains(
                onGrid,
                miner =>
                    (CapsuleerMiningHull)miner.Pilot.ShipTypeId
                        is CapsuleerMiningHull.Hulk
                            or CapsuleerMiningHull.Mackinaw
                            or CapsuleerMiningHull.Skiff
            );
            foreach (var miner in onGrid.Where(miner => miner.Pilot.PilotId != founder))
            {
                Assert.Equal((int)MiningLaserType.StripMinerI, miner.Laser!.TypeId);
                Assert.True(miner.Laser.Online);
            }

            var inventory = host.GetRequiredService<IPilotInventory>();
            saved = Until(
                host,
                fixture.Clock,
                crew,
                miners =>
                    miners.All(miner =>
                        miner.Work.Cycles > 0
                        && inventory.List(miner.Pilot.PilotId, miner.Pilot.ShipId).Any(item => item.TypeId == Veldspar && item.Quantity > 0)
                    )
            );
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        long[] restoredIds = saved.Select(miner => miner.Pilot.PilotId).ToArray();
        reopened.GetRequiredService<IHumanPresence>().Update(observer, HomeSystem);
        reopened.PrepareActivatedPopulations();
        var scheduler = reopened.GetRequiredService<CapsuleerScheduler>();
        Assert.Equal(
            restoredIds,
            reopened
                .GetRequiredService<ICapsuleerCommunity>()
                .Pilots()
                .Where(pilot => pilot.Career == CapsuleerCareer.Mining)
                .Select(pilot => pilot.PilotId)
                .Order()
        );
        foreach (var miner in saved)
        {
            var restored = scheduler.Miner(miner.Pilot.PilotId)!;
            Assert.Equal(miner.Pilot.ShipId, restored.Pilot.ShipId);
            Assert.Equal(
                (miner.Work.WorkingBelt, miner.Work.TargetId, miner.Work.Cycles),
                (restored.Work.WorkingBelt, restored.Work.TargetId, restored.Work.Cycles)
            );
        }
        long belt = saved[0].Work.WorkingBelt;
        scheduler.ObserveAggression(saved[0].Pilot.ShipId);
        var escaping = Until(
            reopened,
            fixture.Clock,
            restoredIds,
            miners =>
                miners.All(miner => miner.Work.Activity is MinerActivity.ReadyToReturn or MinerActivity.Returning or MinerActivity.Docked)
        );
        Assert.Contains(escaping[0].Work.AvoidedBelts, memory => memory.BeltId == belt);
    }

    private static MinerObservation[] Until(ServiceProvider host, TestClock clock, long[] crew, Func<MinerObservation[], bool> condition)
    {
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
        MinerObservation[] miners = [];
        for (int step = 0; step < 600; step++)
        {
            clock.Seconds++;
            maintenance.Tick();
            miners = crew.Select(id => scheduler.Miner(id)!).ToArray();
            if (condition(miners))
            {
                return miners;
            }
        }
        throw new InvalidOperationException(
            string.Join(", ", miners.Select(miner => $"{miner.Pilot.ShipTypeId} {miner.Work.Activity} {miner.WaitingReason}"))
        );
    }
}
