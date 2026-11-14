using Eden.Gameplay.Mining;
using Eden.Modding;
using Eden.World.Storage;

namespace Eden.Server.Modding;

internal static class AutoMiningPackage
{
    internal static void Write(WorldDatabase database, string path)
    {
        const string entry = "client/automining.py";
        using var source = typeof(AutoMiningPackage).Assembly.GetManifestResourceStream("Eden.AutoMining.Client")!;
        using MemoryStream contents = new();
        source.CopyTo(contents);
        ModPackages.Write(
            path,
            new ModManifest
            {
                Id = AutoMiningFilter.ModId,
                Name = "AutoMining",
                Version = "1.2.2",
                CatalogFingerprint = database.Catalog.Identity.SourceFingerprint,
                ClientEntry = entry,
            },
            new Dictionary<string, byte[]> { [entry] = contents.ToArray() }
        );
    }
}
