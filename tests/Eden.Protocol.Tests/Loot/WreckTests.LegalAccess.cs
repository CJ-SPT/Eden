using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Loot;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Loot;

public sealed partial class WreckTests
{
    [Theory]
    [InlineData(false, "suspect")]
    [InlineData(true, "suspect")]
    [InlineData(false, "criminal")]
    [InlineData(true, "criminal")]
    [InlineData(false, "outlaw")]
    [InlineData(true, "outlaw")]
    [InlineData(false, "engagement")]
    [InlineData(true, "engagement")]
    public void LegalOwnerAccessAllowsGreenSafetyLootAndExpiresWithoutAbandoningTheWreck(bool resident, string reason)
    {
        using Fixture f = new(liveWorld: resident);
        var viewer = f.Db.Store.FindPilot(1000002)!;
        long owner = f.Pilot.PilotId;
        long expiry = Now + TimeSpan.TicksPerMinute;
        f.Db.Store.SetPosition(viewer.ShipId, f.Wreck.Position, f.Wreck.SystemId);
        SetSafety(f, viewer.PilotId, ShipSafetyLevel.Full);
        long item = f.Item;
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (reason == "outlaw")
            {
                world.Add(new SavedSecurityStatus { PilotId = owner, Value = CrimewatchRules.OutlawSecurity });
            }
            else if (reason == "engagement")
            {
                world.Add(
                    new SavedEngagement
                    {
                        LowerPilotId = Math.Min(owner, viewer.PilotId),
                        HigherPilotId = Math.Max(owner, viewer.PilotId),
                        ExpiresAt = expiry,
                    }
                );
            }
            else
            {
                world.Add(
                    new SavedCrimewatch
                    {
                        PilotId = owner,
                        SystemId = f.Wreck.SystemId,
                        SuspectUntil = reason == "suspect" ? expiry : 0,
                        CriminalUntil = reason == "criminal" ? expiry : 0,
                    }
                );
            }
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Take(viewer.PilotId, viewer.ShipId, f.Wreck.Id, [item], 101, Now));
        f.Store.Take(viewer.PilotId, viewer.ShipId, f.Wreck.Id, [item], 25, Now);
        Assert.Equal(75, Assert.Single(f.Store.Read(owner, f.Wreck.Id, Now).Items).Quantity);
        Assert.False(Assert.Single(f.Store.List(f.Wreck.SystemId, Now)).Abandoned);
        Assert.Equal(
            CrimewatchTimers.Empty,
            f.Db.Services.GetRequiredService<IPilotCrimewatch>().Read(viewer.PilotId, f.Wreck.SystemId).Timers
        );
        if (reason == "outlaw")
        {
            using WorldDbContext world = new(f.Db.Path);
            world.Set<SavedSecurityStatus>().Find(owner)!.Value = CrimewatchRules.OutlawSecurity + 0.01;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Take(viewer.PilotId, viewer.ShipId, f.Wreck.Id, [item], null, expiry));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(75, saved.Set<OwnedItem>().Find(item)!.Quantity);
        Assert.False(saved.Set<WorldWreck>().Find(f.Wreck.Id)!.Abandoned);
        Assert.Null(saved.Set<SavedCrimewatch>().Find(viewer.PilotId));
    }
}
