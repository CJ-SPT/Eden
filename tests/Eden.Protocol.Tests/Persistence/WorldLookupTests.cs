using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class WorldLookupTests
{
    [Fact]
    public void RegisteredWorldLookupAvoidsPathAllocationsAndRetainsAliasResolution()
    {
        using TestWorld f = new(liveWorld: true);
        var world = f.Services.GetRequiredService<LiveWorld>();
        string canonical = Path.GetFullPath(f.Path);
        string alias = Path.Combine(Path.GetDirectoryName(canonical)!, ".", Path.GetFileName(canonical));
        Assert.Same(world, LiveWorld.Find(alias));
        Assert.Same(world, LiveWorld.Find(canonical));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int lookup = 0; lookup < 10000; lookup++)
        {
            Assert.Same(world, LiveWorld.Find(canonical));
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 128);
    }
}
