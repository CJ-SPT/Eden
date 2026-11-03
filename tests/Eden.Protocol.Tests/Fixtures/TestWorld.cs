using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
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

    // These fixtures replay pre-version-28 migrations; remove the newer live
    // module table as well, so version 31 can rename the historical table.
    internal const string DropTestingMarketSchema = """
        DROP TABLE WorldActiveModule;
        DROP TABLE WorldTestingGrant;
        ALTER TABLE WorldMarketStation DROP COLUMN SeedVersion;
        DROP INDEX IX_WorldMarketOrder_RegionId_TypeId_Bid_PriceCents;
        """;

    public TestWorld(bool emptyWallets = true, TimeProvider? clock = null)
    {
        TestCatalog.Create(new WorldDatabase(Path).CatalogPath);
        WorldInitialization.Initialize(new WorldDatabase(Path));
        if (emptyWallets)
        {
            // Feature tests supply their own budgets. Funding/migration tests use
            // emptyWallets: false to retain the real starting grant and journal.
            using WorldDbContext world = new(Path);
            world.Database.ExecuteSqlRaw("DELETE FROM WorldTestingGrant; DELETE FROM WorldWallet");
        }
        Services = ServerServices.Build(new WorldDatabase(Path), clock);
        Store = Services.GetRequiredService<SqliteWorld>();
    }

    public void Dispose()
    {
        Services.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
