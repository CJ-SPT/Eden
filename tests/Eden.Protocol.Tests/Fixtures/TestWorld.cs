using Eden.Server.Persistence;

namespace Eden.Protocol.Tests.Fixtures;

internal sealed class TestWorld : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eden-{Guid.NewGuid():N}.sqlite");
    public SqliteWorld Store { get; }
    public TestWorld() => Store = new(Path);
    public void Dispose() => File.Delete(Path);
}
