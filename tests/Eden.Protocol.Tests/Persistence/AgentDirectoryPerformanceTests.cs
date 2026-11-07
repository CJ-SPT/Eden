using System.Diagnostics;
using System.Security.Cryptography;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Agents;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Persistence;

public sealed class AgentDirectoryPerformanceTests(ITestOutputHelper output)
{
    private const int PinnedAgentCount = 10966;
    private const string PinnedReplySha256 = "76D41E30A675C293F4C823C8BC846A51A252E07FCBF928D40152A3A53843BF46";

    [Fact]
    public void ReadOnlyFullCatalogColdAndCachedDirectory()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_AGENT_PERFORMANCE_SOURCE");
        if (source is null)
        {
            return;
        }
        WorldDatabase database = new(Path.Combine(source, "world.sqlite"));
        // Initialize EF's model separately from directory materialization, as in a running host.
        using (CatalogDbContext catalog = new(database.CatalogPath))
        {
            Assert.True(catalog.Set<SourceRecord>().Any());
        }
        for (int sample = 0; sample < 3; sample++)
        {
            NpcDirectory directory = new(database);
            long started = Stopwatch.GetTimestamp();
            var agents = directory.Agents();
            output.WriteLine($"Sample {sample}: cold directory {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms.");
            Assert.Equal(PinnedAgentCount, agents.Count);
            Assert.Equal(agents.Count, agents.Select(agent => agent.Id).Distinct().Count());
            using (var metrics = SessionPerformance.Begin("cached directory", output.WriteLine, force: true)!)
            {
                Assert.Same(agents, directory.Agents());
                Assert.NotNull(directory.Character(AgentTestData.AgentId));
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
            }
            byte[] reply = BlueMarshal.Encode(new AgentDirectoryReply(agents));
            string digest = Convert.ToHexString(SHA256.HashData(reply));
            Assert.Equal(PinnedReplySha256, digest);
            output.WriteLine($"Reply bytes={reply.Length}, SHA256={digest}.");
        }
    }
}
