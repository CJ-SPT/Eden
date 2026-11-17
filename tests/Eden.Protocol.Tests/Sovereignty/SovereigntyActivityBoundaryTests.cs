using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Mining;
using Eden.Data.World.Skills;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Sovereignty;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Mining;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyActivityBoundaryTests
{
    private const long RockId = AsteroidDefinitions.FirstAsteroidId + 123;
    private const long NpcId = 4_000_000_000_123;

    [Fact]
    public void FatalPirateSettlementAwardsOneCreditAndCannotBeReplayed()
    {
        using SovereigntyCaptureFixture fixture = new();
        SovereigntyDevelopmentTests.Claim(fixture);
        using (var action = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(
                new WorldNpc
                {
                    Id = NpcId,
                    SystemId = SovereigntyCaptureFixture.SystemId,
                    TypeId = 30380,
                    Role = NpcRole.Pirate,
                    X = 2_000_070_000,
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            action.Commit();
        }
        void Kill(bool commit)
        {
            using var action = fixture.Live.Economy.Begin(new(pilots: [SovereigntyCaptureFixture.PilotId]));
            using CatalogDbContext catalog = new(new WorldDatabase(fixture.World.Path).CatalogPath);
            var result = NpcDamagePersistence.Apply(
                action,
                catalog,
                action.Set<WorldNpc>().Find(NpcId)!,
                action.Pilots.Find(SovereigntyCaptureFixture.PilotId)!,
                fixture.Ship(),
                catalog.FittingAttributes(30380),
                new(1e9, 0, 0, 0),
                fixture.Clock.Now,
                bypassResistances: true
            );
            Assert.True(result.Destroyed);
            if (commit)
            {
                action.Commit();
            }
        }
        Kill(false);
        Assert.Empty(fixture.Live.Economy.Inspect(world => world.Set<SavedSovereigntyActivityEvent>().ToArray()));
        Kill(true);
        Assert.Single(fixture.Live.Economy.Inspect(world => world.Set<SavedSovereigntyActivityEvent>().ToArray()));
        Assert.Throws<InvalidDataException>(() => Kill(true));
        Assert.Equal(50000, fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId).MilitaryPoints);
        Assert.Equal(50000, fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId).MilitaryPoints);
    }

    [Fact]
    public void PaidMiningCreditsActualVolumeAndRetriesCannotDuplicateTheYield()
    {
        using SovereigntyCaptureFixture fixture = new();
        TrainMining(fixture);
        long miner = fixture.Fit(483);
        SovereigntyDevelopmentTests.Claim(fixture);
        AddRock(fixture);
        var mining = fixture.World.Services.GetRequiredService<IPilotMining>();
        var cycle = mining.Begin(SovereigntyCaptureFixture.PilotId, miner, RockId, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(cycle.Laser.DurationMilliseconds));
        var result = mining.Collect(SovereigntyCaptureFixture.PilotId, miner, RockId, new(1, true, new(.5, .5, .5, .5, .5)));
        Assert.True(result.Extracted > 0);
        Assert.Throws<InvalidDataException>(() =>
            mining.Collect(SovereigntyCaptureFixture.PilotId, miner, RockId, new(1, true, new(.5, .5, .5, .5, .5)))
        );
        double volume = (result.Extracted + result.CriticalBonus) * .1;
        Assert.Equal(volume * 7.2, fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId).IndustrialPoints, 5);
    }

    [Fact]
    public void MiningDroneCreditsCollectedOreAndExcludesResidue()
    {
        using SovereigntyCaptureFixture fixture = new();
        SovereigntyDevelopmentTests.Claim(fixture);
        AddRock(fixture);
        MiningYield result;
        using (var action = fixture.Live.Economy.Begin(new(pilots: [SovereigntyCaptureFixture.PilotId])))
        {
            using CatalogDbContext catalog = new(new WorldDatabase(fixture.World.Path).CatalogPath);
            result = PilotMiningStore.CollectDrone(
                action,
                catalog,
                action.Pilots.Find(SovereigntyCaptureFixture.PilotId)!,
                12345,
                action.Set<WorldAsteroid>().Find(RockId)!,
                new(UtilityDroneKind.OreMining, 10000, 60000, 10, ResidueChance: 1, ResidueMultiplier: 1),
                new(1, true, new(.5, .5, .5, 0, .5)),
                fixture.Clock.Now
            );
            action.Commit();
        }
        Assert.True(result.Extracted > 0);
        Assert.True(result.Residue > 0);
        Assert.Null(result.Cargo);
        Assert.Equal(
            (result.Extracted + result.CriticalBonus) * .1 * 7.2,
            fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId).IndustrialPoints,
            5
        );
    }

    private static void AddRock(SovereigntyCaptureFixture fixture)
    {
        fixture.Position(new(2_000_070_000, 0, 0));
        using var action = fixture.Live.Economy.Begin(TransactionParticipants.None);
        action.Add(
            new WorldAsteroid
            {
                Id = RockId,
                SystemId = SovereigntyCaptureFixture.SystemId,
                TypeId = 1230,
                Quantity = 100000,
                X = 2_000_071_000,
                Radius = 100,
            }
        );
        action.Commit();
    }

    private static void TrainMining(SovereigntyCaptureFixture fixture)
    {
        using var action = fixture.Live.Economy.Begin(TransactionParticipants.None);
        var skill =
            action.Set<PilotSkill>().Find(SovereigntyCaptureFixture.PilotId, 3386)
            ?? action.Add(new PilotSkill { PilotId = SovereigntyCaptureFixture.PilotId, TypeId = 3386 }).Entity;
        skill.Points = TrainedSkill.PointsForLevel(1, 5);
        action.Commit();
    }
}
