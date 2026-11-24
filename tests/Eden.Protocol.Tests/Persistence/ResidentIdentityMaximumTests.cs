using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ResidentIdentityMaximumTests
{
    private const long First = 1000;
    private const int Count = 4096;
    private const int Local = 30000144;
    private const int Remote = 30000142;

    [Fact]
    public async Task NpcAllocationSkipsRowsAheadOfAnInterruptedSequence()
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var first = new WorldNpc { SystemId = Local };
        var second = new WorldNpc { SystemId = Remote };
        using (var setup = live.Economy.Begin())
        {
            setup.Add(first);
            setup.Add(second);
            setup.Commit();
        }
        using (live.Enter())
        {
            // Model rows published by a transaction that failed before publishing the sequence.
            live.Economy.Committed(new Dictionary<string, long> { [nameof(WorldNpc)] = first.Id - 1 }, [], null, [], []);
        }

        using (var rejected = live.Economy.Begin())
        {
            Assert.Equal(second.Id + 1, rejected.Add(new WorldNpc { SystemId = Remote }).Entity.Id);
        }
        using (var retry = live.Economy.Begin())
        {
            Assert.Equal(second.Id + 1, retry.Add(new WorldNpc { SystemId = Remote }).Entity.Id);
            Assert.Throws<InvalidDataException>(() => retry.Add(new WorldNpc { Id = first.Id, SystemId = Remote }));
            Assert.Equal(Local, retry.Set<WorldNpc>().Find(first.Id)!.SystemId);
            Assert.Equal(Remote, retry.Set<WorldNpc>().Find(second.Id)!.SystemId);
            retry.Commit();
        }
        await live.FlushAsync();
        fixture.Services.Dispose();
        using var reopened = Eden.Server.ServerServices.Build(new(fixture.Path), liveWorld: true);
        using var resumed = reopened.GetRequiredService<LiveWorld>().Economy.Begin();
        Assert.Equal(second.Id + 2, resumed.Add(new WorldNpc { SystemId = Local }).Entity.Id);
    }

    [Fact]
    public void PilotIdentityMaximumKeepsPrivateActiveShipOwnershipVisibility()
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using var action = live.Economy.Begin(new(systems: [Local, Remote]));
        var pilots = action.Filter<Pilot>(_ => true).OrderBy(pilot => pilot.Id).ToArray();
        Assert.Equal(2, pilots.Length);
        Assert.Equal(pilots[1].Id, action.Pilots.Max(pilot => (long?)pilot.Id));
        pilots[1].ActiveShip.OwnerId = pilots[0].Id;
        Assert.Equal(pilots[0].Id, action.Pilots.Max(pilot => (long?)pilot.Id));
    }

    [Fact]
    public async Task IdentityMaximumRetainsBoundsPrivateChangesRollbackAndReopenedStateWithoutCloningHistory()
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (var setup = live.Economy.Begin(new(systems: [Local, Remote])))
        {
            for (int index = 0; index < Count; index++)
            {
                setup.Add(new SavedExplorationSite { Id = First + index, SystemId = index == Count - 1 ? Remote : Local });
            }
            setup.Commit();
        }
        long highest = First + Count - 1;
        using (var action = live.Economy.Begin(new(systems: [Local, Remote])))
        using (var metrics = SessionPerformance.Begin("resident maximum identity", _ => { }, force: true)!)
        {
            int before = action.Materializations;
            Assert.Equal(highest, action.Filter<SavedExplorationSite>(_ => true).Max(site => (long?)site.Id));
            Assert.Equal(highest, action.Filter<SavedExplorationSite>(site => site.Id >= First).Max(site => (long?)site.Id));
            Assert.Null(action.Filter<SavedExplorationSite>(site => site.Id >= long.MaxValue).Max(site => (long?)site.Id));
            Assert.Equal(before, action.Materializations);

            var added = action.Add(new SavedExplorationSite { Id = highest + 10, SystemId = Local }).Entity;
            Assert.Equal(added.Id, action.Filter<SavedExplorationSite>(_ => true).Max(site => (long?)site.Id));
            action.Remove(added);
            Assert.Equal(highest, action.Filter<SavedExplorationSite>(_ => true).Max(site => (long?)site.Id));
            action.Remove(action.Set<SavedExplorationSite>().Find(highest)!);
            Assert.Equal(highest - 1, action.Filter<SavedExplorationSite>(_ => true).Max(site => (long?)site.Id));
            action.Commit();
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (var rejected = live.Economy.Begin(new(systems: [Local])))
        {
            var row = rejected.Add(new SavedExplorationSite { Id = highest + 20, SystemId = Local }).Entity;
            row.Id++;
            Assert.Equal(row.Id, rejected.Filter<SavedExplorationSite>(_ => true).Max(site => (long?)site.Id));
            Assert.Throws<InvalidDataException>(rejected.Commit);
        }
        Assert.Equal(highest - 1, live.Economy.Inspect(world => world.Filter<SavedExplorationSite>(_ => true).Max(site => (long?)site.Id)));
        await live.FlushAsync();
        using var disk = WorldConnections.FileConnection(fixture.Path);
        using WorldDbContext reopened = new(disk);
        Assert.Equal(highest - 1, reopened.Set<SavedExplorationSite>().Max(site => (long?)site.Id));
    }

    [Fact]
    public void EmptyNegativeAndFilteredIdentityQueriesKeepTheirNullableSemantics()
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using var action = live.Economy.Begin(new(systems: [Local, Remote]));
        Assert.Null(action.Filter<SavedExplorationSite>(_ => true).Max(site => (long?)site.Id));
        action.Add(new SavedExplorationSite { Id = -2, SystemId = Local });
        action.Add(new SavedExplorationSite { Id = -1, SystemId = Remote });
        Assert.Equal(-1, action.Filter<SavedExplorationSite>(_ => true).Max(site => (long?)site.Id));
        Assert.Null(action.Filter<SavedExplorationSite>(site => site.Id >= 0).Max(site => (long?)site.Id));
        Assert.Equal(-2, action.Filter<SavedExplorationSite>(site => site.SystemId == Local).Max(site => (long?)site.Id));
        Assert.Equal(-1, action.Filter<SavedExplorationSite>(site => site.Id < 0).Max(site => site.Id));
        Assert.Equal(9, action.Filter<SavedExplorationSite>(_ => true).Max(site => (long?)site.Id + 10));
        Assert.Null(action.Filter<SavedExplorationSite>(_ => false).Max(site => (long?)site.Id));
    }
}
