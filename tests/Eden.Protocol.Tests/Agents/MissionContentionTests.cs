using System.Diagnostics;
using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionContentionTests(ITestOutputHelper output)
{
    private const int UnrelatedInventoryRows = 8192;
    private const long MaximumAllocationGrowth = 500_000;
    private const int TritaniumType = 34;

    [Fact]
    public async Task ClosingMissionKeepsWorkBoundedWithUnrelatedInventoryAndPreservesContainerBoundaries()
    {
        var small = await Measure(0);
        var populated = await Measure(UnrelatedInventoryRows);
        Assert.InRange(populated, 0, small + MaximumAllocationGrowth);

        async Task<long> Measure(int unrelated)
        {
            using MissionFixture fixture = new(live: true);
            var live = fixture.Services.GetRequiredService<LiveWorld>();
            var offer = fixture.Accept(fixture.Request());
            long first = MissionContainerRules.Id(offer.Id, 0);
            long end = checked(first + MissionRules.NpcsPerMission);
            long[] boundaries;
            using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
            {
                for (int index = 0; index < unrelated; index++)
                {
                    Add(MissionFixture.StationId);
                }
                boundaries = new[] { first - 1, first, end - 1, end }.Select(Add).ToArray();
                action.Commit();

                long Add(long location)
                {
                    var item = action
                        .Add(
                            new OwnedItem
                            {
                                Id = action.NextItemId(),
                                OwnerId = MissionFixture.PilotId,
                                TypeId = TritaniumType,
                                Quantity = 1,
                                LocationId = location,
                                FlagId = (int)InventoryFlag.Cargo,
                            }
                        )
                        .Entity;
                    return item.Id;
                }
            }
            long started = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            using (var metrics = SessionPerformance.Begin("mission closure", output.WriteLine, force: true)!)
            {
                var update = fixture.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Quit, offer.Id);
                Assert.Equal(MissionState.Quit, update.Offer!.State);
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            output.WriteLine(
                $"Unrelated items={unrelated}: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms, {allocated} bytes."
            );
            live.Economy.Inspect(world =>
            {
                Assert.NotNull(world.Set<OwnedItem>().Find(boundaries[0]));
                Assert.Null(world.Set<OwnedItem>().Find(boundaries[1]));
                Assert.Null(world.Set<OwnedItem>().Find(boundaries[2]));
                Assert.NotNull(world.Set<OwnedItem>().Find(boundaries[3]));
                return true;
            });
            await live.FlushAsync();
            using var file = WorldConnections.FileConnection(fixture.Db.Path);
            using WorldDbContext reopened = new(file);
            Assert.Equal(MissionState.Quit, reopened.Set<SavedMission>().Find(offer.Id)!.State);
            Assert.Equal(2, reopened.Set<OwnedItem>().Count(item => boundaries.Contains(item.Id)));
            return allocated;
        }
    }
}
