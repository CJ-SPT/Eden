namespace Eden.World.Catalog;

public sealed class MissingCatalogException(string catalogPath, bool isPackageCatalog)
    : FileNotFoundException("The game data catalog is missing.", catalogPath)
{
    public bool IsPackageCatalog { get; } = isPackageCatalog;
}
