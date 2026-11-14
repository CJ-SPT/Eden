using Eden.Data.World.Combat;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Protocol.Flight;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Loot;

public sealed partial class JettisonTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CriminalJettisonIsPublicAfterTheFlagExpiresAndSurvivesCheckpoint(bool resident)
    {
        Clock clock = new(s_start);
        using TestWorld db = NewWorld(resident, clock);
        var owner = db.Store.FindPilot(AccountId)!;
        long cargo = Seed(db, owner.ShipId, InventoryFlag.Cargo, 100);
        db.Store.SetPosition(owner.ShipId, s_position, SystemId);
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new SavedCrimewatch
                {
                    PilotId = owner.PilotId,
                    SystemId = SystemId,
                    CriminalUntil = Now + 1,
                }
            );
            world.SaveChanges();
        }
        var jettison = db.Services.GetRequiredService<ICargoJettison>();
        var can = jettison.Jettison(owner.PilotId, owner.ShipId, [cargo], Now).Contact!;
        Assert.True(can.Abandoned);
        var wrecks = db.Services.GetRequiredService<IWrecks>();
        var viewer = db.Store.FindPilot(1000002)!;
        db.Store.SetPosition(viewer.ShipId, can.Position, SystemId);
        using FlightSession observer = new(db.Store, db.Store.FindPilot(1000002)!, clock, new(), [], wrecks: wrecks);
        observer.Snapshot();
        Assert.True(Assert.Single(observer.VisibleWrecks).Abandoned);
        if (resident)
        {
            await LiveWorld.Find(db.Path)!.FlushAsync();
        }
        else
        {
            WorldInitialization.Open(db.Path);
        }
        using (WorldDbContext disk = new(WorldConnections.FileConnection(db.Path)))
        {
            Assert.True(disk.Set<SavedJetcan>().Find(can.Id)!.Abandoned);
        }
        Assert.Single(wrecks.Take(viewer.PilotId, viewer.ShipId, can.Id, [cargo], null, Now + 1));
        Assert.Equal(CrimewatchTimers.Empty, db.Services.GetRequiredService<IPilotCrimewatch>().Read(viewer.PilotId, SystemId).Timers);
    }
}
