using Eden.Data.Catalog;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Fixtures;

public sealed class TestWorldSeedTests
{
    [Fact]
    public void FingerprintTracksInputBytesAndIgnoresEnumerationOrder()
    {
        string directory = TestDirectory.Create("eden-seed-inputs-").FullName;
        try
        {
            string code = Path.Combine(directory, "code.dll");
            string data = Path.Combine(directory, "fixture.json");
            File.WriteAllText(code, "first code");
            File.WriteAllText(data, "first data");
            string original = TestWorldSeed.Fingerprint([code, data]);
            Assert.Equal(original, TestWorldSeed.Fingerprint([data, code]));
            File.WriteAllText(data, "changed data");
            Assert.NotEqual(original, TestWorldSeed.Fingerprint([code, data]));
            File.WriteAllText(data, "first data");
            File.WriteAllText(code, "changed code");
            Assert.NotEqual(original, TestWorldSeed.Fingerprint([code, data]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentReadersRecoverDamagedSnapshotsAndMissingManifests()
    {
        string directory = TestDirectory.Create("eden-seed-recovery-").FullName;
        const string fingerprint = "fixture";
        try
        {
            TestWorldSeed original = TestWorldSeed.Load(directory, fingerprint);
            File.WriteAllText(Path.Combine(directory, fingerprint, "world.seed"), "interrupted write");
            TestWorldSeed[] repaired = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ => Task.Run(() => TestWorldSeed.Load(directory, fingerprint)))
            );
            foreach (TestWorldSeed seed in repaired)
            {
                Assert.Equal(original.Catalog, seed.Catalog);
                Assert.Equal(repaired[0].World, seed.World);
            }
            File.Delete(Path.Combine(directory, fingerprint, "checksums"));
            TestWorldSeed recovered = TestWorldSeed.Load(directory, fingerprint);
            CheckWorld(recovered);
            TestWorldSeed changed = TestWorldSeed.Load(directory, "changed-build");
            CheckWorld(changed);
            Assert.True(File.Exists(Path.Combine(directory, "changed-build", "checksums")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ParallelWorldsKeepCatalogAndSavedEditsPrivateAcrossReopen()
    {
        await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(index =>
                    Task.Run(() =>
                    {
                        using TestWorld fixture = new();
                        string name = "Private pilot " + index;
                        using (WorldDbContext world = new(fixture.Path))
                        {
                            world.Pilots.Single(p => p.AccountId == 1000001).Name = name;
                            world.SaveChanges();
                        }
                        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath, writable: true))
                        {
                            catalog.Set<ItemType>().AsTracking().Single(t => t.Id == 601).Name = name;
                            catalog.SaveChanges();
                        }
                        WorldInitialization.Initialize(new WorldDatabase(fixture.Path));
                        Assert.Equal(name, fixture.Store.FindPilot(1000001)!.Name);
                        using CatalogDbContext reopened = new(new WorldDatabase(fixture.Path).CatalogPath);
                        Assert.Equal(name, reopened.Set<ItemType>().Single(t => t.Id == 601).Name);
                    })
                )
        );
        using TestWorld later = new();
        Assert.Equal("Eden Pilot", later.Store.FindPilot(1000001)!.Name);
    }

    private static void CheckWorld(TestWorldSeed seed)
    {
        string directory = TestDirectory.Create("eden-seed-check-").FullName;
        try
        {
            string path = Path.Combine(directory, "world.sqlite");
            File.WriteAllBytes(path, seed.World);
            using WorldDbContext world = new(path);
            Assert.Equal("ok", world.Database.SqlQueryRaw<string>("PRAGMA integrity_check").AsEnumerable().Single());
            Assert.Equal(2, world.Pilots.Count());
            Assert.Equal("Eden Pilot", world.Pilots.Single(p => p.AccountId == 1000001).Name);
            Assert.Equal(0, world.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM WorldWallet").AsEnumerable().Single());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
