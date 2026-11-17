using Eden.Gameplay.Combat;
using Eden.Gameplay.Structures;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureCombatRulesTests
{
    private static readonly long s_now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
    private static readonly DamageVector s_resonance = new(1, 1, 1, 1);
    private static readonly StructureProfile s_structure = new(
        35832,
        "Astrahus",
        1657,
        StructureFamily.Citadel,
        StructureSize.Medium,
        1000,
        null,
        null,
        3,
        100,
        100,
        100,
        1000,
        1000,
        1000,
        false
    );

    private static StructureBattleContext Context(StructureSize size = StructureSize.Medium) =>
        new(
            s_structure with
            {
                Size = size,
            },
            new(new(1000, 1000, 1000, 0, s_resonance, s_resonance, s_resonance), new(100, 100, 100), new(10, 10, 10), true),
            true,
            false,
            false,
            0,
            18,
            0
        );

    private static StructureBattle Battle() =>
        new(StructureState.ShieldVulnerable, s_now, null, new(1000, 1000, 1000), null, 0, new(0, 0, 0), 0);

    [Fact]
    public void MultipleAttackersShareTheLayerCapAndRepairPauseWithoutBankingIdleDamage()
    {
        var context = Context();
        var first = StructureCombatRules.Apply(context, Battle(), new(60, 0, 0, 0), s_now);
        var second = StructureCombatRules.Apply(context, first.Battle, new(80, 0, 0, 0), s_now);
        Assert.Equal(60, first.Applied);
        Assert.Equal(40, second.Applied);
        Assert.Equal(900, second.Battle.Health.Shield);
        Assert.Equal(first.Battle.RepairAt, second.Battle.RepairAt);
        Assert.Equal(0, StructureCombatRules.Apply(context, second.Battle, new(100, 0, 0, 0), s_now).Applied);
        var next = StructureCombatRules.Apply(context, second.Battle, new(10000, 0, 0, 0), s_now + 5 * TimeSpan.TicksPerSecond);
        Assert.Equal(100, next.Applied);
        Assert.Equal(800, next.Battle.Health.Shield);
    }

    [Fact]
    public void BelowThresholdDamageDoesNotPauseAndAnExactDeadlineRepairsBeforeTheNextAttack()
    {
        var context = Context();
        var hit = StructureCombatRules.Apply(context, Battle(), new(5, 0, 0, 0), s_now);
        Assert.Equal(s_now + StructureRules.RepairDuration, hit.Battle.RepairAt);
        Assert.Equal(0, hit.Battle.PausedUntil);
        var repaired = StructureCombatRules.Advance(context, hit.Battle, hit.Battle.RepairAt!.Value);
        Assert.Equal(context.Combat.Defense.Undamaged, repaired.Health);
        Assert.Null(repaired.RepairAt);
        Assert.Equal(995, StructureCombatRules.Apply(context, hit.Battle, new(5, 0, 0, 0), hit.Battle.RepairAt.Value).Battle.Health.Shield);
    }

    [Theory]
    [InlineData(StructureSize.Medium, true, false, StructureState.ArmorReinforced)]
    [InlineData(StructureSize.Medium, false, false, StructureState.ArmorReinforced)]
    [InlineData(StructureSize.Large, true, false, StructureState.ArmorReinforced)]
    [InlineData(StructureSize.Large, false, false, StructureState.ArmorVulnerable)]
    [InlineData(StructureSize.ExtraLarge, true, true, StructureState.ArmorVulnerable)]
    [InlineData(StructureSize.Flex, true, false, StructureState.ArmorVulnerable)]
    public void ShieldDepletionUsesSizePowerAndAbandonmentAndStopsAtAReinforcementBoundary(
        StructureSize size,
        bool fullPower,
        bool abandoned,
        StructureState expected
    )
    {
        var context = Context(size) with { FullPower = fullPower, Abandoned = abandoned };
        var initial = Battle() with { Health = new(100, 1000, 1000) };
        var hit = StructureCombatRules.Apply(context, initial, new(10000, 0, 0, 0), s_now);
        Assert.Equal(expected, hit.Battle.State);
        if (expected == StructureState.ArmorReinforced)
        {
            Assert.Equal(1000, hit.Battle.Health.Armor);
            Assert.Null(hit.Battle.RepairAt);
            Assert.NotNull(hit.Battle.EndsAt);
            Assert.Throws<InvalidDataException>(() => StructureCombatRules.Apply(context, hit.Battle, new(1, 0, 0, 0), s_now));
        }
    }

    [Fact]
    public void MediumArmorFlowsIntoHullWhileLargeArmorGetsASecondReinforcement()
    {
        var armor = Battle() with { State = StructureState.ArmorVulnerable, Health = new(0, 100, 1000) };
        var medium = StructureCombatRules.Apply(Context(), armor, new(200, 0, 0, 0), s_now);
        Assert.Equal(StructureState.HullVulnerable, medium.Battle.State);
        Assert.Equal(900, medium.Battle.Health.Structure);
        var large = StructureCombatRules.Apply(Context(StructureSize.Large), armor, new(200, 0, 0, 0), s_now);
        Assert.Equal(StructureState.HullReinforced, large.Battle.State);
        Assert.Equal(1000, large.Battle.Health.Structure);
    }

    [Fact]
    public void ReinforcementAndRepairDeadlinesAdvanceAfterAColdRestartWithoutReplayingDamage()
    {
        var context = Context();
        var reinforced = StructureCombatRules.Apply(context, Battle() with { Health = new(100, 1000, 1000) }, new(100, 0, 0, 0), s_now);
        long exit = reinforced.Battle.EndsAt!.Value;
        var vulnerable = StructureCombatRules.Advance(context, reinforced.Battle, exit);
        Assert.Equal(StructureState.ArmorVulnerable, vulnerable.State);
        Assert.Equal(exit + StructureRules.RepairDuration, vulnerable.RepairAt);
        var recovered = StructureCombatRules.Advance(context, reinforced.Battle, exit + StructureRules.RepairDuration);
        Assert.Equal(StructureState.ShieldVulnerable, recovered.State);
        Assert.Equal(context.Combat.Defense.Undamaged, recovered.Health);
        Assert.Equal(recovered, StructureCombatRules.Advance(context, recovered, exit + TimeSpan.TicksPerDay));
    }

    [Fact]
    public void NoAutoRepairProfilesRemainDamagedAndAbandonedHullLossIsTerminal()
    {
        var context = Context() with { Combat = Context().Combat with { AutoRepair = false } };
        var hit = StructureCombatRules.Apply(context, Battle(), new(10, 0, 0, 0), s_now);
        Assert.Null(hit.Battle.RepairAt);
        Assert.Equal(hit.Battle, StructureCombatRules.Advance(context, hit.Battle, s_now + TimeSpan.TicksPerDay));
        var hull = Battle() with { State = StructureState.HullVulnerable, Health = new(0, 0, 50) };
        var destroyed = StructureCombatRules.Apply(context with { Abandoned = true }, hull, new(100, 0, 0, 0), s_now);
        Assert.Equal(StructureState.Destroyed, destroyed.Battle.State);
        Assert.True(destroyed.Battle.Health.Destroyed);
        Assert.Null(destroyed.Battle.RepairAt);
    }

    [Fact]
    public void InvalidDamageAndBackwardsClocksLeaveTheInputUnchanged()
    {
        var battle = Battle();
        Assert.Throws<ArgumentOutOfRangeException>(() => StructureCombatRules.Apply(Context(), battle, new(double.NaN, 0, 0, 0), s_now));
        Assert.Throws<ArgumentOutOfRangeException>(() => StructureCombatRules.Apply(Context(), battle, new(-1, 0, 0, 0), s_now));
        Assert.Throws<InvalidDataException>(() => StructureCombatRules.Apply(Context(), battle, new(1, 0, 0, 0), s_now - 1));
        Assert.Equal(1000, battle.Health.Shield);
    }

    [Theory]
    [InlineData(StructureSize.Medium, true, 0, 78)]
    [InlineData(StructureSize.Medium, false, 0.4, 102)]
    [InlineData(StructureSize.Medium, false, 0.5, 126)]
    [InlineData(StructureSize.Large, false, 0, 30)]
    public void ArmorExitUsesThePinnedSecurityDelayThenTheChosenUtcHour(StructureSize size, bool wormhole, double security, int hours)
    {
        var context = Context(size) with { Wormhole = wormhole, Security = security };
        long exit = StructureCombatRules.ReinforcementExit(context, StructureState.ArmorReinforced, s_now);
        Assert.Equal(s_now + hours * TimeSpan.TicksPerHour, exit);
        Assert.Equal(
            exit + TimeSpan.TicksPerHour,
            StructureCombatRules.ReinforcementExit(
                context with
                {
                    ReinforcementJitter = TimeSpan.TicksPerHour,
                },
                StructureState.ArmorReinforced,
                s_now
            )
        );
    }

    [Fact]
    public void CorelessOnliningNeverRepairsAndLowPowerFlexSkipsTheHullReinforcement()
    {
        var context = Context() with { HasCore = false };
        var coreless = Battle() with { State = StructureState.Onlining, Health = new(0, 0, 1000) };
        var hit = StructureCombatRules.Apply(context, coreless, new(20, 0, 0, 0), s_now);
        Assert.Null(hit.Battle.RepairAt);
        Assert.Equal(980, hit.Battle.Health.Structure);
        var flex = Context(StructureSize.Flex) with { FullPower = false };
        var armor = Battle() with { State = StructureState.ArmorVulnerable, Health = new(0, 50, 1000) };
        Assert.Equal(StructureState.HullVulnerable, StructureCombatRules.Apply(flex, armor, new(50, 0, 0, 0), s_now).Battle.State);
    }
}
