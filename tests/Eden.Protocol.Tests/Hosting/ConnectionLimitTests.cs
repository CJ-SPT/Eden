using System.Net;
using Eden.Server;
using Eden.Server.Launcher;

namespace Eden.Protocol.Tests.Hosting;

public sealed class ConnectionLimitTests
{
    [Fact]
    public void PendingLoginsAreLimitedPerAddressAndPromotionReleasesExactlyOnce()
    {
        PendingLogins pending = new(new());
        IPAddress remote = IPAddress.Parse("192.0.2.1");
        var leases = Enumerable.Range(0, 4).Select(_ => pending.TryEnter(remote)!).ToArray();
        try
        {
            Assert.All(leases, lease => Assert.NotNull(lease));
            Assert.Null(pending.TryEnter(remote.MapToIPv6()));
            using var other = pending.TryEnter(IPAddress.Parse("192.0.2.2"));
            Assert.NotNull(other);
            leases[0].Dispose();
            leases[0].Dispose();
            using var replacement = pending.TryEnter(remote);
            Assert.NotNull(replacement);
            Assert.Null(pending.TryEnter(remote));
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public void LoopbackCanUseAllPendingSlotsWhileActiveCapacityIsSeparate()
    {
        LauncherOptions options = new();
        PendingLogins pending = new(options);
        PilotGate gate = new(options);
        var leases = Enumerable.Range(0, options.MaximumPendingLogins).Select(_ => pending.TryEnter(IPAddress.Loopback)!).ToArray();
        try
        {
            Assert.All(leases, lease => Assert.NotNull(lease));
            Assert.Null(pending.TryEnter(IPAddress.Loopback));
            for (long id = 1; id <= options.MaximumActiveSessions; id++)
            {
                Assert.True(gate.TryEnter(id));
            }
            Assert.False(gate.TryEnter(100));
            gate.Leave(1);
            Assert.True(gate.TryEnter(100));
            Assert.False(gate.TryEnter(100));
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(0, 16, 4)]
    [InlineData(8, 0, 4)]
    [InlineData(8, 16, 0)]
    [InlineData(8, 4, 5)]
    public void RejectsInvalidLimits(int active, int pending, int perAddress)
    {
        var options = new LauncherOptions
        {
            MaximumActiveSessions = active,
            MaximumPendingLogins = pending,
            MaximumPendingLoginsPerAddress = perAddress,
        };
        Assert.Throws<InvalidDataException>(options.Validate);
    }
}
