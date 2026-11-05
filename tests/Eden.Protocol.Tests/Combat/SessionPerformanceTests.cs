using Eden.Protocol.PilotSessions;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Combat;

public sealed class SessionPerformanceTests
{
    [Fact]
    public async Task DatabaseCountsFlowAcrossAwaitAndNestedScopesWithoutLeaking()
    {
        using TestWorld db = new();
        List<string> reports = [];
        using (var outer = SessionPerformance.Begin("test", reports.Add, force: true)!)
        {
            await Task.Yield();
            using (var inner = SessionPerformance.Begin("read", reports.Add)!)
            {
                using WorldDbContext world = new(db.Path);
                _ = world.Set<WorldNpc>().AsNoTracking().ToArray();
                Assert.Equal(1, inner.Commands);
                Assert.Equal(1, inner.Connections);
            }
            Assert.True(SessionPerformance.IsActive);
            Assert.Equal(1, outer.Commands);
            Assert.Equal(1, outer.Connections);
        }
        Assert.False(SessionPerformance.IsActive);
        Assert.Equal(2, reports.Count);
    }
}
