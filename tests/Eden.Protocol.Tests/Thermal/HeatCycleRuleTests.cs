using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;

namespace Eden.Protocol.Tests.Thermal;

public sealed class HeatCycleRuleTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    private static HeatRackProfile Profile(ModuleRack rack = ModuleRack.Low) => new(rack, 100, .01, 1, .5);

    private static PaidHeatCycle Cycle(long item, long start, long end, double absorption = .01, ModuleRack rack = ModuleRack.Low) =>
        new(item, rack, start * Second, end * Second, absorption);

    [Fact]
    public void LateAdvancementUsesEachPaidDeadlineAndThenCoolsWithoutInventingCycles()
    {
        var empty = HeatCycleRules.Create([Profile()], 0);
        var started = HeatCycleRules.Start(empty, Cycle(1, 0, 10)).State;
        var done = HeatCycleRules.Advance(started, 20 * Second);
        Assert.Equal(9.516258196404053, Assert.Single(done.Completed).RackHeat, 8);
        Assert.Equal(8.61066649579777, done.State.Heat(ModuleRack.Low), 8);
        Assert.Empty(done.State.Cycles);
        Assert.Empty(HeatCycleRules.Advance(done.State, 30 * Second).Completed);
        Assert.Empty(empty.Cycles);
        Assert.Equal(0, empty.Heat(ModuleRack.Low));
        Assert.Single(started.Cycles);
    }

    [Fact]
    public void OverlappingContributionsUseTheSumOnlyDuringTheirOwnIntervals()
    {
        var first = HeatCycleRules.Start(HeatCycleRules.Create([Profile()], 0), Cycle(1, 0, 20)).State;
        var both = HeatCycleRules.Start(first, Cycle(2, 5, 10, .03)).State;
        var completed = HeatCycleRules.Advance(both, 20 * Second);
        Assert.Equal(new long[] { 2, 1 }, completed.Completed.Select(c => c.Cycle.ModuleId));
        Assert.Equal(22.119921692859506, completed.Completed[0].RackHeat, 8);
        Assert.Equal(29.53119102812866, completed.Completed[1].RackHeat, 8);
        Assert.Equal(29.53119102812866, completed.State.Heat(ModuleRack.Low), 8);
        var stepped = both;
        for (int second = 6; second <= 20; second++)
        {
            stepped = HeatCycleRules.Advance(stepped, second * Second).State;
        }
        Assert.Equal(completed.State.Heat(ModuleRack.Low), stepped.Heat(ModuleRack.Low), 8);
        Assert.Equal(2, both.Cycles.Count);
    }

    [Fact]
    public void RackHeatingIsIndependentAndSimultaneousCompletionsHaveStableIdentityOrder()
    {
        var state = HeatCycleRules.Create([Profile(), Profile(ModuleRack.Medium), Profile(ModuleRack.High)], 0);
        state = HeatCycleRules.Start(state, Cycle(3, 0, 10, .04, ModuleRack.Medium)).State;
        state = HeatCycleRules.Start(state, Cycle(2, 0, 10, .01)).State;
        state = HeatCycleRules.Start(state, Cycle(1, 0, 10, .01)).State;
        var finished = HeatCycleRules.Advance(state, 10 * Second);
        Assert.Equal(new long[] { 1, 2, 3 }, finished.Completed.Select(c => c.Cycle.ModuleId));
        Assert.Equal(18.12692469220182, finished.State.Heat(ModuleRack.Low), 8);
        Assert.Equal(32.96799539643607, finished.State.Heat(ModuleRack.Medium), 8);
        Assert.Equal(0, finished.State.Heat(ModuleRack.High));
    }

    [Fact]
    public void DuplicateRegistrationCannotReheatAndAStaleStopCannotCancelTheNextPaidCycle()
    {
        var cycle = Cycle(1, 0, 10);
        var first = HeatCycleRules.Start(HeatCycleRules.Create([Profile()], 0), cycle).State;
        Assert.Same(first, HeatCycleRules.Start(first, cycle).State);
        var next = HeatCycleRules.Start(first, Cycle(1, 10, 20));
        Assert.Single(next.Completed);
        var stale = HeatCycleRules.Cancel(next.State, cycle.Key, 15 * Second);
        Assert.Equal(Cycle(1, 10, 20), Assert.Single(stale.State.Cycles));
        var cancelled = HeatCycleRules.Cancel(stale.State, Cycle(1, 10, 20).Key, 15 * Second);
        Assert.Empty(cancelled.State.Cycles);
        Assert.Empty(HeatCycleRules.Advance(cancelled.State, 25 * Second).Completed);
        Assert.Equal(
            stale.State.Heat(ModuleRack.Low) * Math.Exp(-.1),
            HeatCycleRules.Advance(cancelled.State, 25 * Second).State.Heat(ModuleRack.Low),
            8
        );
        Assert.Same(stale.State, HeatCycleRules.Advance(stale.State, Second).State);
    }

    [Fact]
    public void ChangingProfilesUsesTheOldRateUntilTheBoundaryAndNeverClampsExistingHeat()
    {
        var state = HeatCycleRules.Start(HeatCycleRules.Create([Profile()], 0), Cycle(1, 0, 10)).State;
        var changed = HeatCycleRules.Configure(state, [Profile() with { Generation = 2 }], 5 * Second).State;
        Assert.Equal(13.92920235749422, HeatCycleRules.Advance(changed, 10 * Second).State.Heat(ModuleRack.Low), 8);
        var hot = HeatCycleRules.Restore(new(0, [new(Profile(), 80)], []));
        var smaller = HeatCycleRules.Configure(hot, [Profile() with { Capacity = 50 }], 0).State;
        Assert.Equal(80, smaller.Heat(ModuleRack.Low));
        var warming = HeatCycleRules.Start(smaller, Cycle(1, 0, 10)).State;
        Assert.Equal(77.14512254107879, HeatCycleRules.Advance(warming, 10 * Second).State.Heat(ModuleRack.Low), 8);
        Assert.Equal(80, HeatCycleRules.Configure(hot, [], 0).State.Heat(ModuleRack.Low));
    }

    [Fact]
    public void RemovingAnUnavailableSourceStopsFutureGenerationWithoutErasingHeat()
    {
        var state = HeatCycleRules.Start(HeatCycleRules.Create([Profile()], 0), Cycle(1, 0, 20)).State;
        state = HeatCycleRules.Advance(state, 10 * Second).State;
        var stopped = HeatCycleRules.RetainSources(state, new HashSet<long>());
        Assert.Empty(stopped.Cycles);
        Assert.Equal(state.Heat(ModuleRack.Low), stopped.Heat(ModuleRack.Low));
        Assert.Equal(8.61066649579777, HeatCycleRules.Advance(stopped, 20 * Second).State.Heat(ModuleRack.Low), 8);
        Assert.Single(state.Cycles);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidAbsorptionCannotEnterAnOtherwiseHealthyState(double invalid)
    {
        var state = HeatCycleRules.Create([Profile()], 0);
        Assert.Throws<InvalidDataException>(() => HeatCycleRules.Start(state, Cycle(1, 0, 10, invalid)));
        Assert.True(state.Idle);
    }

    [Fact]
    public void InvalidTimelinesAndOverflowRejectWithoutMutatingEarlierSnapshots()
    {
        var empty = HeatCycleRules.Create([Profile()], 0);
        Assert.Throws<InvalidDataException>(() => HeatCycleRules.Start(empty, Cycle(1, 10, 10)));
        Assert.Throws<InvalidDataException>(() => HeatCycleRules.Start(empty, Cycle(1, 0, 10, rack: ModuleRack.Rig)));
        var state = HeatCycleRules.Start(empty, Cycle(1, 0, 20)).State;
        Assert.Throws<InvalidDataException>(() => HeatCycleRules.Start(state, Cycle(1, 5, 10)));
        var later = HeatCycleRules.Advance(state, 10 * Second).State;
        Assert.Throws<InvalidDataException>(() => HeatCycleRules.Start(later, Cycle(2, 5, 20)));
        Assert.Throws<InvalidDataException>(() => HeatCycleRules.Advance(empty, -1));
        var huge = HeatCycleRules.Start(empty, Cycle(1, 0, 10, double.MaxValue)).State;
        Assert.Throws<InvalidDataException>(() => HeatCycleRules.Start(huge, Cycle(2, 0, 10, double.MaxValue)));
        Assert.Single(huge.Cycles);
        Assert.Empty(empty.Cycles);
    }

    [Fact]
    public void IdleReadsDoNotCreateNewGenerationsAndRestoredCollectionsAreImmutableCopies()
    {
        List<HeatRackSnapshot> racks = [new(Profile(), 0)];
        List<PaidHeatCycle> cycles = [];
        var restored = HeatCycleRules.Restore(new(0, racks, cycles));
        racks.Clear();
        cycles.Add(Cycle(1, 0, 10));
        Assert.Single(restored.Racks);
        Assert.Empty(restored.Cycles);
        Assert.Same(restored, HeatCycleRules.Advance(restored, 500 * Second).State);
        var cutoff = HeatCycleRules.Restore(new(0, [new(Profile(), .5)], []));
        Assert.Equal(0, HeatCycleRules.Advance(cutoff, Second).State.Heat(ModuleRack.Low));
    }

    [Fact]
    public void FractionalDeadlinesRemainExactAndStoppingResetsSubCutoffHeatInTheSameTick()
    {
        var cycle = new PaidHeatCycle(1, ModuleRack.Low, 0, TimeSpan.TicksPerMillisecond + 1, .01);
        var state = HeatCycleRules.Start(HeatCycleRules.Create([Profile()], 0), cycle).State;
        var before = HeatCycleRules.Advance(state, cycle.CompletesAt - 1);
        Assert.Single(before.State.Cycles);
        Assert.Empty(before.Completed);
        Assert.True(before.State.Heat(ModuleRack.Low) > 0);
        var cancelled = HeatCycleRules.Cancel(before.State, cycle.Key, cycle.CompletesAt - 1);
        Assert.Equal(0, cancelled.State.Heat(ModuleRack.Low));
        Assert.Empty(cancelled.State.Cycles);
        var done = HeatCycleRules.Advance(before.State, cycle.CompletesAt);
        Assert.Equal(cycle.Key, Assert.Single(done.Completed).Cycle.Key);
        Assert.True(done.Completed[0].RackHeat > before.State.Heat(ModuleRack.Low));
        Assert.Equal(0, done.State.Heat(ModuleRack.Low));
    }

    [Fact]
    public void LateDamageConsumersCanInterruptAnotherSourceBeforeAdvancingTheRestOfTheFrame()
    {
        var state = HeatCycleRules.Start(HeatCycleRules.Create([Profile()], 0), Cycle(1, 0, 10)).State;
        state = HeatCycleRules.Start(state, Cycle(2, 0, 20)).State;
        var first = HeatCycleRules.AdvanceToNextDeadline(state, 30 * Second);
        Assert.Equal(10 * Second, first.State.UpdatedAt);
        Assert.Equal(1, Assert.Single(first.Completed).Cycle.ModuleId);
        Assert.Equal(2, Assert.Single(first.State.Cycles).ModuleId);
        // The damage owner interrupts module 2 at module 1's completion.
        var interrupted = HeatCycleRules.RetainSources(first.State, new HashSet<long>());
        var remainder = HeatCycleRules.AdvanceToNextDeadline(interrupted, 30 * Second);
        Assert.Empty(remainder.Completed);
        Assert.Empty(remainder.State.Cycles);
        Assert.Equal(first.State.Heat(ModuleRack.Low) * Math.Exp(-.2), remainder.State.Heat(ModuleRack.Low), 8);
    }
}
