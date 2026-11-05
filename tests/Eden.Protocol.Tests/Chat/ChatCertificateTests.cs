using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Chat;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Chat;

public sealed class ChatCertificateTests
{
    [Fact]
    public void HostRestartRetainsTheSamePublicCertificateAndPreparedClientTrustIsIdempotent()
    {
        using TestWorld world = new();
        var first = world.Services.GetRequiredService<ChatCertificate>();
        using ChatCertificate reopened = new(new WorldDatabase(world.Path), TimeProvider.System);
        Assert.Equal(first.PublicPem, reopened.PublicPem);
        Assert.True(reopened.Certificate.HasPrivateKey);
        Assert.DoesNotContain("PRIVATE KEY", first.PublicPem);
        string root = Path.Combine(Path.GetDirectoryName(world.Path)!, "prepared-client");
        string bundle = Path.Combine(root, "tq", "bin64", "packages", "certifi", "cacert.pem");
        Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
        File.WriteAllText(bundle, "existing public roots\n");
        Assert.Throws<InvalidDataException>(() => first.TrustClient(root));
        Assert.Equal("existing public roots\n", File.ReadAllText(bundle));
        File.WriteAllText(Path.Combine(root, ".eden-client-ready"), "3662168");
        first.TrustClient(root);
        string updated = File.ReadAllText(bundle);
        Assert.StartsWith("existing public roots\n", updated);
        Assert.Contains(first.PublicPem, updated);
        reopened.TrustClient(root);
        Assert.Equal(updated, File.ReadAllText(bundle));
    }
}
