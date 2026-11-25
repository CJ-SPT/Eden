using Eden.Data.World.Contracts;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Information;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Information;

public sealed partial class InformationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCargoScanShowsOnlyImmediateCourierEscrowAssets(bool failed)
    {
        const int standardContainer = 3297;
        const int tritanium = 34;
        const int veldspar = 1230;
        const int originalBlueprint = 1130;
        const int copyBlueprint = 1131;
        using var f = Prepare();
        CourierTestData.Prepare(f.Db);
        TestCatalog.AddSchool(f.CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (9,'Blueprint');
                INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (12,'Cargo Container',2),(167,'Missile Blueprint',9);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Capacity) VALUES
                    (3297,'Small Standard Container',12,120),
                    (1130,'Iron Charge S Blueprint',167,0),
                    (1131,'Tungsten Charge S Blueprint',167,0);
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/types','3297',json_object('volume',10)),
                    ('client/types','1130',json_object('volume',0.01)),
                    ('client/types','1131',json_object('volume',0.01));
                """
            );
        }
        long scanner = f.Fit(442);
        WorldDatabase database = new(f.Db.Path);
        using LiveWorld live = new(database, f.Clock, _ => { });
        var target = f.Db.Store.FindPilot(1000002)!;
        var creator = f.Db.Services.GetRequiredService<ICharacterCreation>();
        long issuer = creator.Create(1000002, new("Cargo Manifest Issuer", 1, 1, 0, CharacterRules.UnspecifiedAncestry, [], [], 18));
        long visible;
        long outer;
        long hidden;
        long original;
        long copy;
        long blueprintStack;
        using (WorldActionScope setup = new(database, new(pilots: [issuer], ships: [target.ShipId])))
        {
            var wallet = setup.World.Set<PilotWallet>().Find(issuer) ?? setup.World.Add(new PilotWallet { PilotId = issuer }).Entity;
            wallet.Cents = 100_000_000;
            visible = Grant(tritanium, 25, ContractAssetTestData.Station, InventoryFlag.Hangar, false);
            outer = Grant(standardContainer, 1, ContractAssetTestData.Station, InventoryFlag.Hangar, true);
            hidden = Grant(veldspar, 71, outer, InventoryFlag.None, false);
            original = Grant(originalBlueprint, 1, ContractAssetTestData.Station, InventoryFlag.Hangar, true);
            copy = Grant(copyBlueprint, 1, ContractAssetTestData.Station, InventoryFlag.Hangar, true, runs: 5);
            blueprintStack = Grant(originalBlueprint, 2, ContractAssetTestData.Station, InventoryFlag.Hangar, false);
            setup.Commit();

            long Grant(int type, int amount, long location, InventoryFlag flag, bool singleton, int runs = -1)
            {
                long id = setup.World.NextItemId();
                setup.World.Add(
                    new OwnedItem
                    {
                        Id = id,
                        TypeId = type,
                        OwnerId = issuer,
                        LocationId = location,
                        FlagId = (int)flag,
                        Quantity = amount,
                        Singleton = singleton,
                        BlueprintRuns = runs,
                    }
                );
                return id;
            }
        }
        var contracts = f.Db.Services.GetRequiredService<IPilotContracts>();
        long contract = contracts.Create(
            issuer,
            CourierTestData.Draft(visible) with
            {
                Items = [new(visible, 25), new(outer, 1), new(original, 1), new(copy, 1), new(blueprintStack, 2)],
            }
        );
        long package = contracts.Accept(target.PilotId, contract).Courier!.PackageId!.Value;
        Assert.Equal(target.ShipId, CourierTestData.Load(f.Db, package).LocationId);
        if (failed)
        {
            contracts.Fail(target.PilotId, contract);
        }
        Space(f);
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        using var scope = f.Db.Services.CreateScope();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<InformationHandler>();
        Assert.NotEqual(issuer, source.Pilot.PilotId);
        Assert.NotEqual(target.PilotId, source.Pilot.PilotId);
        Lock(f, source, targeting, target.ShipId);
        Assert.IsType<PilotCallResult.Success>(handler.CallModule(source, "Activate", [scanner, "cargoScan", target.ShipId, 0L]));
        Assert.Empty(source.Notifications.OfType<CargoScanCompleted>());
        f.Clock.Milliseconds += (long)Math.Ceiling(handler.ActiveEffect(source.Pilot, scanner)!.DurationMilliseconds!.Value);
        handler.Advance(source);
        handler.Advance(source);
        var cargo = Assert.Single(source.Notifications.OfType<CargoScanCompleted>()).Scan.Cargo;
        Assert.Contains(new ScanItem(tritanium, 25), cargo);
        Assert.Contains(new ScanItem(standardContainer, 1), cargo);
        Assert.Contains(new ScanItem(originalBlueprint, -1), cargo);
        Assert.Contains(new ScanItem(copyBlueprint, -2), cargo);
        Assert.Contains(new ScanItem(originalBlueprint, 2), cargo);
        Assert.DoesNotContain(cargo, row => row.TypeId is CourierRules.PackageType or veldspar);
        Assert.Empty(recipient.Notifications.OfType<CargoScanCompleted>());

        WorldActionScope.Inspect(
            database,
            world =>
            {
                Assert.Equal(failed ? ContractStatus.Failed : ContractStatus.InProgress, world.Set<SavedContract>().Find(contract)!.Status);
                Assert.Equal(target.ShipId, world.Set<OwnedItem>().Find(package)!.LocationId);
                Assert.Equal(contract, world.Set<SavedContractEscrow>().Find(hidden)!.ContractId);
                Assert.Null(world.Set<OwnedItem>().Find(hidden));
            }
        );
    }
}
