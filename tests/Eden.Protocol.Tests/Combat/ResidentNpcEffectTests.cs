using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Gameplay.Combat;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class NpcEwarTests
{
    [Fact]
    public void ResidentNpcDisengagementCancelsThePaidEffectAndReopensWithoutSql()
    {
        const int JammingNpc = 2385;
        using Fixture f = new(JammingNpc, resident: true);
        Assert.Single(f.Advance());
        var live = LiveWorld.Find(f.Combat.Db.Path)!;
        live.Economy.Warm();
        long ship = NpcCombatTests.Fixture.ShipId;
        ResidentMonitorAssertions.WithoutSql(() => f.Combat.Combat.Disengage(ship));
        Assert.Empty(f.Warfare.Incoming(ship));
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Empty(reopened.Set<WorldNpcEwar>());
        Assert.DoesNotContain(reopened.Set<NpcAggression>(), row => row.TargetShipId == ship);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResidentNpcEffectCyclesRejectAndRetryWithoutSqlAndCheckpointTheirPaidState(bool simulationObjects)
    {
        const int NeutralizerNpc = 11901;
        using Fixture f = new(NeutralizerNpc, resident: true, simulationObjects: simulationObjects);
        var first = Assert.Single(f.Advance());
        var live = LiveWorld.Find(f.Combat.Db.Path)!;
        live.Economy.Warm();
        _ = f.Fitting.ShipAttributes(NpcCombatTests.Fixture.PilotId);
        long ship = NpcCombatTests.Fixture.ShipId;
        long source = f.Combat.Npc.Id;
        long rolls = f.Roll.Count;
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            for (int frame = 0; frame < 32; frame++)
            {
                Assert.Equal(first, Assert.Single(f.Advance()));
            }
        });
        Assert.Equal(rolls, f.Roll.Count);
        double before = live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(ship)!.Charge);
        f.Combat.Clock.AdvanceWallTime(TimeSpan.FromTicks(first.ReadyAt - f.Now));
        f.Roll.Values.Enqueue(double.NaN);
        WithoutWorldSql(() => Assert.Throws<InvalidDataException>(() => f.Advance()));
        Assert.Equal(before, live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(ship)!.Charge));
        Assert.Equal(
            first.ReadyAt,
            live.Economy.Inspect(world => world.Set<WorldNpcEwar>().Find(source, (long)first.Ability.Kind)!.ReadyAt)
        );
        using (var rejected = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            rejected.Project();
            using WorldDbContext saved = new(rejected.Database);
            Assert.Equal(before, saved.Set<ShipEnergy>().Find(ship)!.Charge);
            Assert.Equal(first.ReadyAt, Assert.Single(saved.Set<WorldNpcEwar>()).ReadyAt);
        }
        NpcEwarContact? next = null;
        WithoutWorldSql(() => next = Assert.Single(f.Advance()));
        var paid = Assert.IsType<NpcEwarContact>(next);
        Assert.True(paid.ReadyAt > first.ReadyAt);
        Assert.Equal(f.Now, paid.StartedAt);
        double remaining = live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(ship)!.Charge);
        Assert.Equal(before - paid.Applied!.CapacitorDrain, remaining, 8);
        WithoutWorldSql(() => f.Warfare.Cancel(ship));
        Assert.Empty(f.Warfare.Incoming(ship));
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Empty(reopened.Set<WorldNpcEwar>());
        Assert.Equal(remaining, reopened.Set<ShipEnergy>().Find(ship)!.Charge, 8);
    }

    private static void WithoutWorldSql(Action action)
    {
        List<string> commands = [];
        using var metrics = SessionPerformance.Begin("resident NPC paid effect", _ => { }, force: true, command: commands.Add)!;
        action();
        Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
        Assert.Equal(0, metrics.Connections);
        Assert.InRange(metrics.FittingCalculations, 0, 1);
    }
}
