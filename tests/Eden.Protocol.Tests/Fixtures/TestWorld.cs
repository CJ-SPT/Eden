using Eden.Server;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Fixtures;

internal sealed class TestWorld : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eden-{Guid.NewGuid():N}.sqlite");
    public SqliteWorld Store { get; }
    public ServiceProvider Services { get; }
    public TestWorld()
    {
        Services = ServerServices.Build(new WorldDatabase(Path));
        Store = Services.GetRequiredService<SqliteWorld>();
    }

    public void Dispose()
    {
        Services.Dispose();
        File.Delete(Path);
    }
}
