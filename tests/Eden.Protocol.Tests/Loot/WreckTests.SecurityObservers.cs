using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.World.Runtime.Economy;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Loot;

public sealed partial class WreckTests
{
    [Fact]
    public void OwnerSecurityChangesRefreshWreckAndJetcanSlimsWithoutSqlOrUnrelatedInvalidation()
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        long item = f.Item;
        f.Store.Take(pilot.PilotId, pilot.ShipId, f.Wreck.Id, [item], null, Now);
        var can = f.Db.Services.GetRequiredService<ICargoJettison>().Jettison(pilot.PilotId, pilot.ShipId, [item], Now).Contact!;
        var viewer = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(viewer.ShipId, f.Wreck.Position, f.Wreck.SystemId);
        Clock clock = new(s_start);
        using FlightSession observer = new(f.Db.Store, f.Db.Store.FindPilot(1000002)!, clock, new(), [], wrecks: f.Store);
        observer.Snapshot();
        observer.Updates.Clear();
        var before = f.Store.List(f.Wreck.SystemId, Now);
        Assert.All(before, contact => Assert.Equal(0, contact.OwnerSecurityStatus));
        using (WorldActionScope action = new(new(f.Db.Path)))
        {
            action.World.Add(new SavedSecurityStatus { PilotId = pilot.PilotId, Value = CrimewatchRules.OutlawSecurity });
            action.Commit();
        }
        using (var metrics = SessionPerformance.Begin("owner security observer", _ => { }, force: true)!)
        {
            observer.RefreshWrecks();
            Assert.All(observer.VisibleWrecks, contact => Assert.Equal(CrimewatchRules.OutlawSecurity, contact.OwnerSecurityStatus));
            var updates = observer
                .WireUpdates()
                .Cast<object?[]>()
                .Select(u => (object?[])u[1]!)
                .Where(a => Equals(a[0], "OnSlimItemChange"));
            Assert.Equal(2, updates.Count());
            foreach (var update in updates)
            {
                var args = Assert.IsType<object?[]>(update[1]);
                var fields = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(args[1]).State);
                Assert.Equal(CrimewatchRules.OutlawSecurity, fields["securityStatus"]);
            }
            Assert.NotEmpty(BlueMarshal.Encode(observer.WireUpdates()));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.All(before, contact => Assert.Equal(0, contact.OwnerSecurityStatus));
        var current = f.Store.List(f.Wreck.SystemId, Now);
        using (WorldActionScope rollback = new(new(f.Db.Path)))
        {
            rollback.World.Set<SavedSecurityStatus>().Find(pilot.PilotId)!.Value = 0;
        }
        using (WorldActionScope unrelated = new(new(f.Db.Path)))
        {
            unrelated.World.Add(new SavedSecurityStatus { PilotId = viewer.PilotId, Value = 1 });
            unrelated.Commit();
        }
        var after = f.Store.List(f.Wreck.SystemId, Now);
        Assert.Same(current.Single(w => w.Id == f.Wreck.Id), after.Single(w => w.Id == f.Wreck.Id));
        Assert.Same(current.Single(w => w.Id == can.Id), after.Single(w => w.Id == can.Id));
        Assert.All(after, contact => Assert.Equal(CrimewatchRules.OutlawSecurity, contact.OwnerSecurityStatus));
    }
}
