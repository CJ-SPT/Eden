using Eden.Infrastructure.Registration;
using Eden.World.Storage;

namespace Eden.World.Catalog;

[Injectable(InjectionType.Singleton)]
internal sealed class CatalogLifetime : IDisposable
{
    private readonly IDisposable _retention;

    public CatalogLifetime(WorldDatabase database)
    {
        _retention = CatalogProvider.Retain(database.CatalogPath);
    }

    public void Dispose() => _retention.Dispose();
}
