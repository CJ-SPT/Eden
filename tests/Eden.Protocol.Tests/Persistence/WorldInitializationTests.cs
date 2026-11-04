using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class WorldInitializationTests
{
    [Fact]
    public void ResolvingWorldReaderDoesNotInitializeOrMigrateTheDatabase()
    {
        string directory = TestDirectory.Create("eden-init-").FullName;
        try
        {
            WorldDatabase database = new(System.IO.Path.Combine(directory, "world.sqlite"));
            TestCatalog.Create(database.CatalogPath);
            using var services = ServerServices.Build(database);
            _ = services.GetRequiredService<SqliteWorld>();
            Assert.False(File.Exists(database.Path));
            WorldInitialization.Initialize(database);
            WorldInitialization.Initialize(database);
            Assert.True(File.Exists(database.Path));
            Assert.Single(services.GetRequiredService<SqliteWorld>().ListPilots(1000001));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
