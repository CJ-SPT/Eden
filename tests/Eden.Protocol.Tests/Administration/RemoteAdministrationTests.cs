using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Eden.Server.Administration;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Administration;

public sealed partial class AdministrationTests
{
    [Fact]
    public async Task RemoteAdministrationServesTheConfiguredHttpsOriginWithAdminAuthentication()
    {
        const string host = "eden.example.test";
        const string authority = host + ":26003";
        const string origin = "https://" + authority;
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName(host);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        string certificatePath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx));
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, presented, _, errors) =>
                    presented?.Thumbprint == certificate.Thumbprint && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0,
            };
            await using var fixture = new Fixture(false, origin, certificatePath, handler);
            await fixture.App.StartAsync();
            var endpoint = new Uri(Assert.Single(fixture.App.Urls));
            Assert.Equal("https", endpoint.Scheme);
            Assert.Equal("0.0.0.0", endpoint.Host);
            fixture.Client.BaseAddress = new UriBuilder(endpoint) { Host = "127.0.0.1" }.Uri;

            using var wrongHost = await fixture.Client.GetAsync("/login");
            Assert.Equal(HttpStatusCode.Forbidden, wrongHost.StatusCode);
            fixture.Client.DefaultRequestHeaders.Host = host;
            using var wrongPort = await fixture.Client.GetAsync("/login");
            Assert.Equal(HttpStatusCode.Forbidden, wrongPort.StatusCode);

            fixture.Client.DefaultRequestHeaders.Host = authority;
            using var denied = await fixture.Client.GetAsync("/api/admin/configuration");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            fixture.Client.DefaultRequestHeaders.Add("Origin", "http://" + authority);
            using var wrongScheme = await fixture.Client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
            Assert.Equal(HttpStatusCode.Forbidden, wrongScheme.StatusCode);
            fixture.Client.DefaultRequestHeaders.Remove("Origin");
            fixture.Client.DefaultRequestHeaders.Add("Origin", origin);

            await fixture.SignIn();
            var cookies = handler.CookieContainer.GetCookies(fixture.Client.BaseAddress).Cast<Cookie>().ToArray();
            Assert.Equal(2, cookies.Length);
            Assert.All(cookies, cookie => Assert.True(cookie.Secure));
            Assert.Contains("World settings", await fixture.Client.GetStringAsync("/"));
            using var negotiation = await fixture.Client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
            Assert.Equal(HttpStatusCode.OK, negotiation.StatusCode);
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }

    [Theory]
    [InlineData("http://eden.example.test:26003", "server.pfx")]
    [InlineData("https://eden.example.test:26003/admin", "server.pfx")]
    [InlineData("https://eden.example.test:26003", null)]
    public void RemoteAdministrationRequiresAnHttpsOriginAndTlsCertificate(string origin, string? certificatePath)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.Throws<InvalidDataException>(() =>
            AdministrationHost.Create(services, "world.sqlite", 0, origin, IPAddress.Any, certificatePath)
        );
    }
}
