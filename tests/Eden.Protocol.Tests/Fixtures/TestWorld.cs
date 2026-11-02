using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Fixtures;

internal sealed class TestWorld : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("eden-world-").FullName;
    public string Path => System.IO.Path.Combine(_directory, "world.sqlite");
    public SqliteWorld Store { get; }
    public ServiceProvider Services { get; }
    public IReadOnlyList<StaticSpaceObject> Objects => Services.GetRequiredService<ISystemObjects>().Load(30000142);
    public TestWorld()
    {
        TestCatalog.Create(new WorldDatabase(Path).CatalogPath);
        Services = ServerServices.Build(new WorldDatabase(Path));
        Store = Services.GetRequiredService<SqliteWorld>();
    }

    public void Dispose()
    {
        Services.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
