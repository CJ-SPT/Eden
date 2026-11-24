using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Loot;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Combat;

public sealed class RetiredNpcTrafficTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReclaimsExpiredAmbientNpcRowsAndPreservesCurrentTrafficAndHistory(bool startupMigration)
    {
        const int system = 30000142;
        const int otherSystem = 30000144;
        const long miningEncounter = 515;
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock);
        long old = clock.Now - WreckDefinitions.LifetimeSeconds * TimeSpan.TicksPerSecond;
        var recent = Convoy(system, old + 1);
        var destroyed = Convoy(system, old);
        destroyed.DestroyedAt = old;
        var active = Convoy(system, null);
        var wrecked = Convoy(system, old);
        var remote = Convoy(otherSystem, old);
        var expired = Convoy(system, old);
        WorldNpc hauler = new()
        {
            AnchorKind = NpcAnchorKind.Belt,
            Role = NpcRole.MiningHauler,
            EncounterId = miningEncounter,
            SystemId = system,
            RetiredAt = old,
        };
        using (WorldDbContext initial = new(fixture.Path))
        {
            initial.AddRange(recent, destroyed, active, wrecked, remote, expired, hauler);
            initial.Add(new WorldNpcEncounter { Id = miningEncounter, SystemId = system });
            initial.SaveChanges();
            initial.Add(new WorldNpcMiningOperation { EncounterId = miningEncounter, SystemId = system });
            initial.SaveChanges();
            initial.Add(new WorldNpcMiningMember { NpcId = hauler.Id, EncounterId = miningEncounter });
            initial.Add(
                new WorldNpcMiningCargo
                {
                    NpcId = hauler.Id,
                    TypeId = 34,
                    Quantity = 7,
                }
            );
            initial.Add(
                new WorldWreck
                {
                    Id = WreckDefinitions.FirstWreckId,
                    NpcId = wrecked.Id,
                    SystemId = system,
                    OwnerId = fixture.Store.FindPilot(1000001)!.PilotId,
                    Name = "Previous loss",
                    ExpiresAt = clock.Now + TimeSpan.TicksPerMinute,
                    SalvagedAt = old,
                }
            );
            initial.SaveChanges();
            if (startupMigration)
            {
                initial.Database.ExecuteSqlRaw("PRAGMA user_version=221");
            }
        }

        if (startupMigration)
        {
            WorldInitialization.Initialize(new(fixture.Path), clock);
        }
        else
        {
            using LiveWorld live = new(new(fixture.Path), clock);
            live.Economy.Warm();
            using (var metrics = SessionPerformance.Begin("retired traffic", _ => { }, force: true)!)
            using (var action = live.Economy.Begin(new(systems: [system])))
            {
                RetiredNpcTraffic.Prune(action, system, clock.Now);
                action.Commit();
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
            }
            await live.FlushAsync();
        }

        using WorldDbContext reopened = new(fixture.Path);
        var npcs = reopened.Set<WorldNpc>();
        Assert.NotNull(npcs.Find(recent.Id));
        Assert.NotNull(npcs.Find(destroyed.Id));
        Assert.NotNull(npcs.Find(active.Id));
        Assert.NotNull(npcs.Find(wrecked.Id));
        Assert.Equal(!startupMigration, npcs.Find(remote.Id) is not null);
        Assert.Null(npcs.Find(expired.Id));
        Assert.Null(npcs.Find(hauler.Id));
        Assert.Empty(reopened.Set<WorldNpcMiningMember>());
        Assert.Empty(reopened.Set<WorldNpcMiningCargo>());
        Assert.Equal(miningEncounter, Assert.Single(reopened.Set<WorldNpcMiningOperation>()).EncounterId);
        Assert.Equal(wrecked.Id, Assert.Single(reopened.Set<WorldWreck>()).NpcId);
        Assert.Empty(reopened.Database.SqlQueryRaw<string>("SELECT \"table\" AS Value FROM pragma_foreign_key_check"));
        Assert.Equal(WorldMigration.CurrentVersion, reopened.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());

        static WorldNpc Convoy(int system, long? retired) =>
            new()
            {
                AnchorKind = NpcAnchorKind.Station,
                Role = NpcRole.Convoy,
                SystemId = system,
                RetiredAt = retired,
            };
    }
}
