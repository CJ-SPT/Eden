namespace Eden.Data.Catalog;

public sealed class CatalogEntry<T>
{
    public string Key { get; set; } = "";
    public T Value { get; set; } = default!;
}
