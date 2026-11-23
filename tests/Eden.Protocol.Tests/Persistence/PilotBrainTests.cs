using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Targeting;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PilotBrainTests
{
    private sealed record Tuning(WorldSettings Settings) : IWorldTuning;

    [Fact]
    public void WorldRatesUseNativeLiteralModifiersWithoutInventingTrainedSkills()
    {
        var policy = new IndustryPolicy(2, 4, 5, 10);
        Skills skills = new() { Bonuses = WorldTuningRules.IndustryBonuses(policy) };
        var tuning = new Tuning(new() { Resources = new(OreYieldMultiplier: 2, IceYieldMultiplier: 6, GasYieldMultiplier: 4) });
        var source = new PilotBrain(skills, new Clock(), skills, skills, skills, tuning: tuning);
        var state = source.Read(90000001, 1000000000001);
        Assert.All(state.CharacterEffects, effect => Assert.Empty(effect.Skills));
        var manufacturing = Assert.Single(state.CharacterEffects, e => e.Attribute == DogmaAttributeId.ManufactureTimeMultiplier);
        Assert.Equal(-50, manufacturing.Value);
        Assert.Equal(BrainOperation.PostPercent, manufacturing.Operation);
        var ore = Assert.Single(
            state.ShipEffects,
            e => e.Attribute == DogmaAttributeId.MiningAmount && e.Extras.SequenceEqual([(int)MiningSkill.Mining])
        );
        var ice = Assert.Single(state.ShipEffects, e => e.Extras.SequenceEqual([(int)MiningSkill.IceHarvesting]));
        Assert.Equal(2, ore.Value);
        Assert.Equal(6, ice.Value);
        Assert.Contains(state.ShipEffects, e => e.Extras.SequenceEqual([InventoryDefinitions.GasScoopGroup]) && e.Value == 4);
        var wire = State(BlueMarshal.Decode(BlueMarshal.Encode(ice)));
        Assert.Equal("LRS"u8.ToArray(), wire["modifierType"]);
        Assert.Equal(4L, wire["operation"]);
        Assert.Empty(Assert.IsType<List<object?>>(wire["skills"]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EarnedShipAttributeSkillsMatchTheAuthoritativeFit(int level)
    {
        const long shipId = 1000000000001;
        (int SkillId, DogmaAttributeId Attribute, double PercentPerLevel)[] expected =
        [
            ((int)FittingSkill.CpuManagement, DogmaAttributeId.CpuOutput, 5),
            ((int)FittingSkill.PowerGridManagement, DogmaAttributeId.PowerOutput, 5),
            ((int)FittingSkill.CapacitorManagement, DogmaAttributeId.CapacitorCapacity, 5),
            ((int)FittingSkill.CapacitorSystemsOperation, DogmaAttributeId.CapacitorRechargeTime, -5),
            ((int)TargetingSkill.LongRangeTargeting, DogmaAttributeId.MaxTargetRange, 5),
            ((int)TargetingSkill.SignatureAnalysis, DogmaAttributeId.ScanResolution, 5),
            ((int)DefenseSkill.Mechanics, DogmaAttributeId.StructureHitPoints, 5),
            ((int)DefenseSkill.HullUpgrades, DogmaAttributeId.ArmorHitPoints, 5),
            ((int)DefenseSkill.ShieldManagement, DogmaAttributeId.ShieldCapacity, 5),
            ((int)DefenseSkill.ShieldOperation, DogmaAttributeId.ShieldRechargeTime, -5),
        ];
        Skills skills = new()
        {
            Values = expected.Select(bonus => new TrainedSkill(bonus.SkillId, TrainedSkill.PointsForLevel(1, level), 1)).ToArray(),
        };
        var source = new PilotBrain(skills, new Clock(), skills, skills, skills);
        var update = source.Update(90000001, shipId);
        var matter = Assert.IsType<object?[]>(BlueMarshal.Decode(update.GrayMatter));
        var effects = Assert.IsType<List<object?>>(matter[1]).Select(State).ToArray();
        Assert.Equal(level == 0 ? 0 : expected.Length, effects.Length);
        var basis = expected.ToDictionary(bonus => bonus.Attribute, _ => 200d);
        var authoritative = FittingRules.Apply(basis, skills.Values.ToDictionary(skill => skill.TypeId, skill => skill.Level), []);
        foreach (var bonus in expected)
        {
            double percent = 0;
            if (level > 0)
            {
                var effect = Assert.Single(effects, effect => Convert.ToInt32(effect["toAttribID"]) == (int)bonus.Attribute);
                Assert.Equal(shipId, effect["toItemID"]);
                Assert.Equal("M"u8.ToArray(), Assert.IsType<byte[]>(effect["modifierType"]));
                Assert.Equal(6L, effect["operation"]);
                Assert.Empty(Assert.IsType<object?[]>(effect["extras"]));
                Assert.Equal((long)bonus.SkillId, Assert.Single(Assert.IsType<List<object?>>(effect["skills"])));
                percent = Assert.IsType<double>(effect["value"]);
                Assert.Equal(bonus.PercentPerLevel * level, percent);
            }
            Assert.Equal(basis[bonus.Attribute] * (1 + percent / 100), authoritative[bonus.Attribute], 8);
        }
    }

    private sealed class Skills : IPilotSkillReader, IPilotIndustrySkills, IPilotNavigationSkills, IPilotJumpCloneSkills
    {
        public IReadOnlyList<TrainedSkill> Values { get; set; } = [];

        public IReadOnlyList<TrainedSkill> ReadSkills(long pilotId) => Values;

        public IReadOnlyList<IndustryCharacterBonus> Bonuses { get; set; } = [];

        public IReadOnlyList<IndustryCharacterBonus> CharacterBonuses(long pilotId) => Bonuses;

        public IReadOnlyList<NavigationBonus> NavigationBonuses(long pilotId) => [];

        public JumpCloneSkills ReadCloneSkills(long pilotId) => new(0, 0, 0, 0);

        public double MaxSpeed(long pilotId) => 295;
    }

    private static Dictionary<string, object?> State(object? value)
    {
        var effect = Assert.IsType<BlueNewObject>(value);
        Assert.Equal("eve.common.script.dogma.effect.BrainEffect", effect.Name);
        return Assert.IsType<Dictionary<string, object?>>(effect.State);
    }

    [Fact]
    public void EarnedSkillsUseNativeShipModifiersWithSeparateWeaponRequirements()
    {
        Skills skills = new()
        {
            Values =
            [
                new((int)CombatSkill.Gunnery, 8000, 1),
                new((int)CombatSkill.MotionPrediction, 2830, 2),
                new((int)CombatSkill.SmallHybridTurret, 250, 1),
                new((int)CombatSkill.RapidFiring, 0, 2),
                new(3327, 256000, 1),
            ],
        };
        Clock clock = new(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        var brain = new PilotBrain(skills, clock, skills, skills, skills).Read(90000001, 1000000000001);
        Assert.Equal(clock.UtcNow.UtcDateTime.ToFileTimeUtc(), brain.Version);
        var effects = Assert
            .IsType<List<object?>>(WireAssertions.Materialize(new BlueList<BrainEffect>(brain.ShipEffects)))
            .Select(State)
            .ToArray();
        Assert.Equal(3, effects.Length);
        Assert.All(
            effects,
            e =>
            {
                Assert.Null(e["fromAttrib"]);
                Assert.Equal(1000000000001L, e["toItemID"]);
                Assert.Equal("LRS", e["modifierType"]);
                Assert.Equal(6, e["operation"]);
            }
        );
        var cycle = Assert.Single(effects, e => Equals(e["toAttribID"], (int)DogmaAttributeId.WeaponCycleTime));
        Assert.Equal(-6d, cycle["value"]);
        Assert.Equal(new object?[] { (int)CombatSkill.Gunnery }, cycle["extras"]);
        var tracking = Assert.Single(effects, e => Equals(e["toAttribID"], (int)DogmaAttributeId.TrackingSpeed));
        Assert.Equal(10d, tracking["value"]);
        Assert.Equal(new List<object?> { (int)CombatSkill.MotionPrediction }, tracking["skills"]);
        var hybrid = Assert.Single(effects, e => Equals(e["toAttribID"], (int)DogmaAttributeId.DamageMultiplier));
        Assert.Equal(5d, hybrid["value"]);
        Assert.Equal(new object?[] { (int)CombatSkill.SmallHybridTurret }, hybrid["extras"]);
        Assert.Empty(Assert.IsType<List<object?>>(WireAssertions.Materialize(new BlueList<BrainEffect>(brain.CharacterEffects))));
        Assert.Empty(Assert.IsType<List<object?>>(WireAssertions.Materialize(new BlueList<BrainEffect>(brain.StructureEffects))));
    }

    [Fact]
    public void BrainUpdatesCarryVersionedGrayMatterAndRecomputeEarnedLevels()
    {
        Skills skills = new() { Values = [new((int)CombatSkill.Gunnery, 250, 1)] };
        Clock clock = new(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        PilotBrain source = new(skills, clock, skills, skills, skills);
        var first = source.Update(90000001, 1000000000001);
        var firstMatter = Assert.IsType<object?[]>(BlueMarshal.Decode(Assert.IsType<byte[]>(first.GrayMatter)));
        Assert.Equal(-2d, State(Assert.Single(Assert.IsType<List<object?>>(firstMatter[1])))["value"]);
        clock.UtcNow = clock.UtcNow.AddSeconds(1000);
        skills.Values = [new((int)CombatSkill.Gunnery, 1415, 1)];
        var next = source.Update(90000001, 1000000000001);
        Assert.True(Assert.IsType<long>(next.Version) > Assert.IsType<long>(first.Version));
        var matter = Assert.IsType<object?[]>(BlueMarshal.Decode(Assert.IsType<byte[]>(next.GrayMatter)));
        Assert.Equal(-4d, State(Assert.Single(Assert.IsType<List<object?>>(matter[1])))["value"]);
        Assert.Empty(Assert.IsType<List<object?>>(matter[0]));
        Assert.Empty(Assert.IsType<List<object?>>(matter[2]));
        Assert.Equal(1415, Assert.Single(skills.Values).Points);
    }

    [Fact]
    public void SurgicalStrikeUsesSeparateNativeGroupModifiersForSupportedTurretFamilies()
    {
        Skills skills = new() { Values = [new((int)CombatSkill.SurgicalStrike, 8000, 1)] };
        var brain = new PilotBrain(skills, new Clock(), skills, skills, skills).Read(90000001, 1000000000001);
        var effects = Assert
            .IsType<List<object?>>(WireAssertions.Materialize(new BlueList<BrainEffect>(brain.ShipEffects)))
            .Select(State)
            .ToArray();
        Assert.Equal(3, effects.Length);
        Assert.All(
            effects,
            e =>
            {
                Assert.Equal("LG", e["modifierType"]);
                Assert.Equal((int)DogmaAttributeId.DamageMultiplier, e["toAttribID"]);
                Assert.Equal(9d, e["value"]);
            }
        );
        Assert.Equal(
            new object?[]
            {
                CombatDefinitions.EnergyWeaponGroup,
                CombatDefinitions.HybridWeaponGroup,
                CombatDefinitions.ProjectileWeaponGroup,
            },
            effects.Select(e => Assert.Single(Assert.IsType<object?[]>(e["extras"])))
        );
    }
}
