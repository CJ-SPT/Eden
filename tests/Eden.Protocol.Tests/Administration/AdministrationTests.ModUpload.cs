using Eden.Modding;
using Eden.Server.Administration;
using Eden.World.Modding;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Administration;

public sealed partial class AdministrationTests
{
    [Fact]
    public async Task BrowserUploadInstallsValidatedModForCurrentAdministrator()
    {
        await using var fixture = await Fixture.Start();
        await fixture.SignIn();
        string html = await fixture.Client.GetStringAsync("/mods");
        Assert.Contains("type=\"file\"", html);
        Assert.Contains("accept=\".edenmod\"", html);

        using var scope = fixture.App.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<AdministrationOperations>();
        var store = fixture.World.Services.GetRequiredService<ModStore>();
        var principal = fixture.Accounts.Authenticate("Corey", Password)!;
        string source = Path.Combine(Path.GetDirectoryName(fixture.World.Path)!, "example.edenmod");
        ModPackages.Write(
            source,
            new()
            {
                Id = "test.upload",
                Name = "Uploaded mod",
                CatalogFingerprint = store.Reference().CatalogFingerprint,
            },
            new Dictionary<string, byte[]>()
        );
        var file = new BrowserModFile(await File.ReadAllBytesAsync(source));
        File.Delete(source);

        string packages = Path.Combine(store.Root, "packages");
        await Assert.ThrowsAsync<IOException>(() => operations.UploadMod(principal, new BrowserModFile([], ModPackages.MaximumBytes + 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => operations.UploadMod(principal, new BrowserModFile([1, 2, 3])));
        Assert.False(Directory.Exists(packages));

        string hash = await operations.UploadMod(principal, file);
        Assert.Equal(ModPackages.Hash(file.Bytes), hash);
        string installed = Assert.Single(Directory.GetFiles(packages));
        Assert.Equal(Path.Combine(packages, hash + ".edenmod"), installed);
        Assert.Equal(file.Bytes, await File.ReadAllBytesAsync(installed));
        Assert.Empty((await operations.Mods(principal)).Enabled);
        Assert.Equal(hash, await operations.UploadMod(principal, file));

        fixture.Accounts.SetPassword("Corey", "rotated administrator password");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operations.UploadMod(principal, file));
    }

    private sealed record BrowserModFile(byte[] Bytes, long? DeclaredSize = null) : IBrowserFile
    {
        public string Name => "example.edenmod";
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => DeclaredSize ?? Bytes.LongLength;
        public string ContentType => "application/octet-stream";

        public Stream OpenReadStream(long maxAllowedSize, CancellationToken cancellationToken)
        {
            if (Size > maxAllowedSize)
            {
                throw new IOException("The upload exceeds its size limit.");
            }
            return new MemoryStream(Bytes, writable: false);
        }
    }
}
