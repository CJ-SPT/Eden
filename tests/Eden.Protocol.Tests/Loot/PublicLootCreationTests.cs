using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Loot;
using Eden.Protocol.Tests.Combat;
using Eden.World.Catalog;
using Eden.World.Loot;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Loot;

public sealed class PublicLootCreationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CriminalNpcLootIsPublicInBothCreationPaths(bool resident)
    {
        using TestWorld db = new(liveWorld: resident);
        NpcEncounterTests.Prepare(db);
        long now = DateTime.UtcNow.ToFileTimeUtc();
        var npc = db.Services.GetRequiredService<INpcEncounters>().List(30000144, now)[0];
        var pilot = db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new SavedCrimewatch
                {
                    PilotId = pilot.PilotId,
                    SystemId = npc.SystemId,
                    CriminalUntil = now + 1,
                }
            );
            world.SaveChanges();
        }
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        if (resident)
        {
            using WorldActionScope action = new(new(db.Path));
            WreckStore.Create(
                action.World,
                catalog,
                action.World.Set<WorldNpc>().Find(npc.Id)!,
                action.World.Pilots.Find(pilot.PilotId)!,
                now
            );
            action.Commit();
        }
        else
        {
            using WorldDbContext world = new(db.Path);
            using var transaction = world.Database.BeginTransaction();
            WreckStore.Create(world, catalog, world.Set<WorldNpc>().Find(npc.Id)!, world.Pilots.Find(pilot.PilotId)!, now);
            world.SaveChanges();
            transaction.Commit();
        }
        Assert.True(Assert.Single(db.Services.GetRequiredService<IWrecks>().List(npc.SystemId, now + 1)).Abandoned);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task CriminalAndOutlawShipLossCreatesPermanentlyPublicWrecks(bool resident, bool criminal)
    {
        using NpcCombatTests.Fixture f = new(resident, useTestClock: true);
        f.Aggress();
        f.Clock.Milliseconds = 7500;
        f.AlmostLost();
        long now = f.Clock.Now;
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (criminal)
            {
                var state = world.Set<SavedCrimewatch>().Find(NpcCombatTests.Fixture.PilotId)!;
                state.CriminalUntil = now + TimeSpan.TicksPerMinute;
            }
            else
            {
                var status = world.Set<SavedSecurityStatus>().Find(NpcCombatTests.Fixture.PilotId);
                status ??= world.Add(new SavedSecurityStatus { PilotId = NpcCombatTests.Fixture.PilotId }).Entity;
                status.Value = CrimewatchRules.OutlawSecurity;
            }
            world.SaveChanges();
        }
        Assert.NotNull(f.Fire()!.Capsule);
        var wrecks = f.Db.Services.GetRequiredService<IWrecks>();
        var wreck = Assert.Single(wrecks.List(f.Npc.SystemId, now));
        Assert.True(wreck.Abandoned);
        Assert.Equal(NpcCombatTests.Fixture.PilotId, wreck.OwnerId);
        if (resident)
        {
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        }
        else
        {
            WorldInitialization.Open(f.Db.Path);
        }
        using WorldDbContext disk = new(WorldConnections.FileConnection(f.Db.Path));
        Assert.True(disk.Set<WorldWreck>().Find(wreck.Id)!.Abandoned);
        Assert.True(Assert.Single(wrecks.List(f.Npc.SystemId, now + TimeSpan.TicksPerMinute)).Abandoned);
    }
}
