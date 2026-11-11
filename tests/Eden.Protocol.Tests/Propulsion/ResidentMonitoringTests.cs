using Eden.Data.World.Combat;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Tests.Combat;
using Eden.World.Runtime;

namespace Eden.Protocol.Tests.Propulsion;

public sealed partial class TackleTests
{
    [Fact]
    public void ResidentMonitorTackleViewsKeepUnrelatedSnapshotsAndRemoveCommittedEffectsWithoutSql()
    {
        const int Web = 526;
        using var f = Prepare(Web);
        long module = f.Fit(Web);
        Space(f);
        long target = Wing(f);
        long ship = f.ShipId;
        long pilot = f.PilotId;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        Activate(f, module, DogmaEffect.StasisWebifier, target);
        live.Economy.Warm();
        var reads = live.Reads!;
        var before = reads.Incoming(target);
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.True(reads.HasTackleWork());
            Assert.Equal(module, Assert.Single(reads.Incoming(target)).Module.ItemId);
            Assert.Empty(reads.Incoming(ship));
        });
        using (var rejected = live.Economy.Begin(new(pilots: [pilot], ships: [target])))
        {
            rejected.Set<WorldTackle>().Find(module)!.StartedAt++;
            Assert.Equal(before[0].StartedAt + 1, Assert.Single(reads.Incoming(target)).StartedAt);
        }
        Assert.Same(before, reads.Incoming(target));
        using (var unrelated = live.Economy.Begin(new(pilots: [pilot])))
        {
            unrelated.Set<ModuleCooldown>().Find(module)!.ReadyAt++;
            unrelated.Commit();
        }
        Assert.Same(before, reads.Incoming(target));
        using (var removal = live.Economy.Begin(new(pilots: [pilot], ships: [target])))
        {
            removal.Remove(removal.Set<WorldTackle>().Find(module)!);
            removal.Commit();
        }
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.Empty(reads.Incoming(target));
            Assert.False(reads.HasTackleWork());
        });
    }
}
