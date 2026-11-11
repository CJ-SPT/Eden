using Eden.Gameplay.Mining;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Mining;

public sealed partial class MiningCrystalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LedgerSeparatesActualCrystalWasteFromCollectedOreIncludingWasteOnlyDepletion(bool wasteOnly)
    {
        using MiningFixture f = new();
        f.Load();
        f.Undock();
        if (wasteOnly)
        {
            using Eden.World.Storage.WorldDbContext world = new(f.F.Db.Path);
            world.Set<Eden.Data.World.Mining.WorldAsteroid>().Find(Asteroid)!.Quantity = 1;
            world.SaveChanges();
        }
        f.Mining.Begin(f.F.PilotId, f.Module, Asteroid, f.F.Clock.Now);
        var result = f.Mining.Collect(f.F.PilotId, f.Module, Asteroid, new(1, true, Rolls with { Residue = 0 }));
        Assert.True(result.Residue > 0);
        if (wasteOnly)
        {
            Assert.Equal(0, result.Extracted);
            Assert.Null(result.Cargo);
        }
        var ledger = f.F.Db.Services.GetRequiredService<IPersonalMiningLedger>();
        Assert.Equal(
            new(result.Extracted + (long)result.CriticalBonus, result.CriticalBonus, result.Residue),
            Assert.Single(ledger.Read(f.F.PilotId)).Quantity
        );
    }
}
