using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Fixtures;

internal sealed class TestWorld : IDisposable
{
    // Historical migrations reconstruct the original WorldNpc schema first.
    internal const string DropNpcNavigationColumns = """
        ALTER TABLE WorldNpc DROP COLUMN VelocityX;
        ALTER TABLE WorldNpc DROP COLUMN VelocityY;
        ALTER TABLE WorldNpc DROP COLUMN VelocityZ;
        ALTER TABLE WorldNpc DROP COLUMN MotionAt;
        ALTER TABLE WorldNpc DROP COLUMN Navigation;
        """;
    private readonly string _directory = Directory.CreateTempSubdirectory("eden-world-").FullName;
    public string Path => System.IO.Path.Combine(_directory, "world.sqlite");
    public SqliteWorld Store { get; }
    public ServiceProvider Services { get; }
    public IReadOnlyList<StaticSpaceObject> Objects => Services.GetRequiredService<ISystemObjects>().Load(30000142);

    public TestWorld()
    {
        TestCatalog.Create(new WorldDatabase(Path).CatalogPath);
        WorldInitialization.Initialize(new WorldDatabase(Path));
        Services = ServerServices.Build(new WorldDatabase(Path));
        Store = Services.GetRequiredService<SqliteWorld>();
    }

    public void Dispose()
    {
        Services.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
