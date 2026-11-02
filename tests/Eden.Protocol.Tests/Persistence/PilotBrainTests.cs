using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PilotBrainTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Skills : IPilotSkillReader
    {
        public IReadOnlyList<TrainedSkill> Values { get; set; } = [];
        public IReadOnlyList<TrainedSkill> ReadSkills(long pilotId) => Values;
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
            Values = [new((int)CombatSkill.Gunnery, 8000, 1), new((int)CombatSkill.MotionPrediction, 2830, 2),
                new((int)CombatSkill.SmallHybridTurret, 250, 1), new((int)CombatSkill.RapidFiring, 0, 2), new(3327, 256000, 1)],
        };
        Clock clock = new();
        var brain = new PilotBrain(skills, clock).Read(90000001, 1000000000001);
        Assert.Equal(clock.Now.UtcDateTime.ToFileTimeUtc(), brain[0]);
        var effects = Assert.IsType<List<object?>>(brain[2]).Select(State).ToArray();
        Assert.Equal(3, effects.Length);
        Assert.All(effects, e =>
        {
            Assert.Null(e["fromAttrib"]);
            Assert.Equal(1000000000001L, e["toItemID"]);
            Assert.Equal("LRS", e["modifierType"]);
            Assert.Equal(6, e["operation"]);
        });
        var cycle = Assert.Single(effects, e => Equals(e["toAttribID"], (int)DogmaAttributeId.WeaponCycleTime));
        Assert.Equal(-6d, cycle["value"]);
        Assert.Equal(new object?[] { (int)CombatSkill.Gunnery }, cycle["extras"]);
        var tracking = Assert.Single(effects, e => Equals(e["toAttribID"], (int)DogmaAttributeId.TrackingSpeed));
        Assert.Equal(10d, tracking["value"]);
        Assert.Equal(new List<object?> { (int)CombatSkill.MotionPrediction }, tracking["skills"]);
        var hybrid = Assert.Single(effects, e => Equals(e["toAttribID"], (int)DogmaAttributeId.DamageMultiplier));
        Assert.Equal(5d, hybrid["value"]);
        Assert.Equal(new object?[] { (int)CombatSkill.SmallHybridTurret }, hybrid["extras"]);
        Assert.Empty(Assert.IsType<List<object?>>(brain[1]));
        Assert.Empty(Assert.IsType<List<object?>>(brain[3]));
    }

    [Fact]
    public void BrainUpdatesCarryVersionedGrayMatterAndRecomputeEarnedLevels()
    {
        Skills skills = new() { Values = [new((int)CombatSkill.Gunnery, 250, 1)] };
        Clock clock = new();
        PilotBrain source = new(skills, clock);
        var first = source.Update(90000001, 1000000000001);
        var firstMatter = Assert.IsType<object?[]>(BlueMarshal.Decode(Assert.IsType<byte[]>(first[1])));
        Assert.Equal(-2d, State(Assert.Single(Assert.IsType<List<object?>>(firstMatter[1])))["value"]);
        clock.Now = clock.Now.AddSeconds(1000);
        skills.Values = [new((int)CombatSkill.Gunnery, 1415, 1)];
        var next = source.Update(90000001, 1000000000001);
        Assert.True(Assert.IsType<long>(next[0]) > Assert.IsType<long>(first[0]));
        var matter = Assert.IsType<object?[]>(BlueMarshal.Decode(Assert.IsType<byte[]>(next[1])));
        Assert.Equal(-4d, State(Assert.Single(Assert.IsType<List<object?>>(matter[1])))["value"]);
        Assert.Empty(Assert.IsType<List<object?>>(matter[0]));
        Assert.Empty(Assert.IsType<List<object?>>(matter[2]));
        Assert.Equal(1415, Assert.Single(skills.Values).Points);
    }

    [Fact]
    public void SurgicalStrikeUsesSeparateNativeGroupModifiersForSupportedTurretFamilies()
    {
        Skills skills = new() { Values = [new((int)CombatSkill.SurgicalStrike, 8000, 1)] };
        var brain = new PilotBrain(skills, new Clock()).Read(90000001, 1000000000001);
        var effects = Assert.IsType<List<object?>>(brain[2]).Select(State).ToArray();
        Assert.Equal(2, effects.Length);
        Assert.All(effects, e =>
        {
            Assert.Equal("LG", e["modifierType"]);
            Assert.Equal((int)DogmaAttributeId.DamageMultiplier, e["toAttribID"]);
            Assert.Equal(9d, e["value"]);
        });
        Assert.Equal(new object?[] { CombatDefinitions.EnergyWeaponGroup, CombatDefinitions.HybridWeaponGroup },
            effects.Select(e => Assert.Single(Assert.IsType<object?[]>(e["extras"]))));
    }
}
