using Eden.Data.World.Characters;
using Eden.Data.World.Mining;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Mining;

public sealed partial class MiningTests
{
    [Fact]
    public async Task SkillPointCallWithDueMiningCycleKeepsUpdatesResidentAndCheckpointsOnce()
    {
        const long account = 1000001;
        using Fixture f = new(useTestClock: true);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT INTO TypeAttribute VALUES (3386,180,165),(3386,181,166)");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<Account>().Find(account)!.CloneGrade = AccountCloneGrade.Omega;
            world.SaveChanges();
        }
        using ResidentFitCache fits = new(new(f.Db.Path));
        var (context, _, _, module) = f.Client(locked: false);
        var live = LiveWorld.Find(f.Db.Path)!;
        live.Economy.Warm();
        using var scope = f.Db.Services.CreateScope();
        var services = scope.ServiceProvider;
        var targeting = services.GetRequiredService<TargetingHandler>();
        var mining = services.GetRequiredService<MiningHandler>();
        var updates = services.GetRequiredService<PilotUpdateCoordinator>();
        var points = services.GetRequiredService<IPilotFreeSkillPoints>();
        var operations = services.GetRequiredService<IPilotOperationScope>();
        updates.Enter(context);
        targeting.Call(context, "dogmaIM", "AddTarget", [Fixture.Asteroid]);
        f.Clock.Milliseconds += 4000;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(Fixture.Asteroid));
        List<byte[]> packets = [];
        PilotSessionOutput output = new(
            context,
            account,
            account,
            packet =>
            {
                packets.Add(BlueMarshal.Encode(packet));
                return Task.CompletedTask;
            },
            _ => { }
        );
        PilotSessionCalls calls = new(
            context,
            output,
            services.GetRequiredService<PilotCallRouter>(),
            updates,
            services.GetRequiredService<SkillHandler>(),
            f.Clock,
            _ => { },
            account,
            account,
            operations
        );
        var binding = context.Reference("@skills");
        var training = services.GetRequiredService<IPilotTraining>().Save(context.Pilot.PilotId, [new(3386, 2)], true);
        Assert.Single(training.Queue);
        services.GetRequiredService<SkillHandler>().Apply(context, training, true);
        services.GetRequiredService<PilotBrain>().Read(context.Pilot.PilotId, context.Pilot.ShipId);
        await calls.ProcessAsync(PilotServicesTests.Request(null, "ApplyFreeSkillPointsToQueue", [], binding.Id));
        live.Economy.Read(
            world =>
            {
                PilotTrainingStore.CreditSilentFreePoints(world, context.Pilot.PilotId, 100);
                return true;
            },
            new(pilots: [context.Pilot.PilotId])
        );
        await calls.ProcessAsync(PilotServicesTests.Request(null, "GetFreeSkillPoints", [], binding.Id));
        await calls.ProcessAsync(PilotServicesTests.Request("dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 100L]));
        Assert.True(mining.IsActive(module));
        f.Clock.Milliseconds += 10_000;
        packets.Clear();
        List<string> sql = [];
        using (var metrics = SessionPerformance.Begin("skill points and paid mining update", _ => { }, force: true, command: sql.Add)!)
        {
            await calls.ProcessAsync(PilotServicesTests.Request(null, "ApplyFreeSkillPointsToQueue", [], binding.Id));
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, sql));
            Assert.Equal(0, metrics.Connections);
            Assert.InRange(metrics.FittingCalculations, 0, 1);
        }
        Assert.True(mining.IsActive(module));
        var notifications = packets.Select(bytes => IncomingMachoPacket.Read(BlueMarshal.Decode(bytes))).ToArray();
        Assert.Single(notifications, packet => packet.Name.EndsWith(".CallRsp", StringComparison.Ordinal));
        var names = notifications
            .Where(packet => packet.Name.EndsWith(".Notification", StringComparison.Ordinal))
            .Select(packet => BlueMarshal.Text(packet.Destination()[1]))
            .ToArray();
        Assert.Single(names, name => name == "OnOreMined");
        Assert.Single(names, name => name == "OnFreeSkillPointsChanged");
        Assert.Single(names, name => name == "OnServerSkillsChanged");
        long remaining = live.Economy.Inspect(world => world.Set<WorldAsteroid>().Find(Fixture.Asteroid)!.Quantity);
        Assert.Equal(9937, remaining);
        long earned = MiningSkillPointRewardTests.Expected(1230, 63);
        Assert.Equal(earned, points.Balance(context.Pilot.PilotId));
        using (var metrics = SessionPerformance.Begin("same tick mining rejection and cancellation", _ => { }, force: true)!)
        {
            mining.Advance(context);
            Assert.Throws<InvalidDataException>(() => f.Store.Begin(context.Pilot.PilotId, module, Fixture.Asteroid, f.Clock.Now));
            f.Store.Stop(context.Pilot.PilotId, module, f.Clock.Now - 1);
            mining.Cancel(context);
            Assert.Throws<InvalidDataException>(() =>
                f.Store.Collect(context.Pilot.PilotId, module, Fixture.Asteroid, new(1, true, new(.5, .5, .5, .5, .5)))
            );
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.Equal(remaining, live.Economy.Inspect(world => world.Set<WorldAsteroid>().Find(Fixture.Asteroid)!.Quantity));
        await live.FlushAsync();
        f.Disconnect(context);
        f.Reopen();
        Assert.Equal(9937, f.Remaining());
        Assert.Equal(earned, points.Balance(90000001));
        Assert.Equal(
            63,
            Assert.Single(services.GetRequiredService<Eden.Gameplay.Mining.IPersonalMiningLedger>().Read(90000001)).Quantity.Collected
        );
    }
}
