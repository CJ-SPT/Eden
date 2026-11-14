using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed partial class StructureCompressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedConversionRollsBackFuelAtItsDeadlineAndTheStackThenRetryCommitsOnce(bool zeroGas)
    {
        using Fixture f = new();
        var facility = Prepare(f);
        var gas = GasPair(f);
        long item = HangarItem(f, facility.Id, zeroGas ? gas.Compressed : Veldspar, zeroGas ? 1 : 100);
        var compression = f.Db.Services.GetRequiredService<IStructureCompression>();
        f.Clock.UtcNow = DateTimeOffset.FromFileTime(facility.Modules.Single(m => m.TypeId == ReprocessingModule).NextFuelAt!.Value);
        string before = State();
        using (WorldDbContext world = new(f.Db.Path))
        {
            // SQLite trigger DDL cannot bind parameters. This literal is a server-assigned long ID.
            string sql = zeroGas
                ? FormattableString.Invariant(
                    $"""
                    CREATE TRIGGER FailCompression BEFORE DELETE ON WorldItem WHEN OLD.Id={item}
                    BEGIN SELECT RAISE(ABORT,'forced compression failure'); END;
                    """
                )
                : FormattableString.Invariant(
                    $"""
                    CREATE TRIGGER FailCompression BEFORE UPDATE ON WorldItem WHEN OLD.Id={item} AND NEW.TypeId!=OLD.TypeId
                    BEGIN SELECT RAISE(ABORT,'forced compression failure'); END;
                    """
                );
            world.Database.ExecuteSqlRaw(sql);
        }
        Assert.Throws<DbUpdateException>(() => Convert());
        Assert.Equal(before, State());
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER FailCompression");
        }
        var result = Convert();
        Assert.Equal(zeroGas ? 0 : 100, result.OutputQuantity);
        string committed = State();
        Assert.NotEqual(before, committed);
        Assert.Throws<InvalidDataException>(() => Convert());
        Assert.Equal(committed, State());
        WorldInitialization.Open(f.Db.Path);
        Assert.Equal(committed, State());

        StructureCompressionResult Convert() =>
            zeroGas ? compression.DecompressGas(f.PilotId, item) : compression.Compress(f.PilotId, item);

        string State()
        {
            using WorldDbContext world = new(f.Db.Path);
            return JsonSerializer.Serialize(
                new
                {
                    Items = world.Set<OwnedItem>().OrderBy(i => i.Id).ToArray(),
                    Modules = world.Set<SavedStructureService>().OrderBy(m => m.ItemId).ToArray(),
                }
            );
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResidentConversionProjectsFuelAndTheStackTogetherAfterCheckpointFailure(bool zeroGas)
    {
        using Fixture f = new(liveWorld: true);
        var facility = Prepare(f);
        var gas = GasPair(f);
        long item = HangarItem(f, facility.Id, zeroGas ? gas.Compressed : Veldspar, zeroGas ? 1 : 100);
        var compression = f.Db.Services.GetRequiredService<IStructureCompression>();
        long warmItem = HangarItem(f, facility.Id, zeroGas ? gas.Compressed : Veldspar, zeroGas ? 1 : 100);
        _ = zeroGas ? compression.DecompressGas(f.PilotId, warmItem) : compression.Compress(f.PilotId, warmItem);
        var live = LiveWorld.Find(f.Db.Path)!;
        var module = facility.Modules.Single(m => m.TypeId == ReprocessingModule);
        f.Clock.UtcNow = DateTimeOffset.FromFileTime(module.NextFuelAt!.Value);
        using (live.EnterShared("compression baseline"))
        {
            live.Economy.Flush();
        }
        int resultType;
        List<string> commands = [];
        using (var measured = SessionPerformance.Begin("resident compression", _ => { }, force: true, command: commands.Add)!)
        {
            var result = zeroGas ? compression.DecompressGas(f.PilotId, item) : compression.Compress(f.PilotId, item);
            resultType = result.OutputTypeId;
            Assert.Equal(zeroGas ? 0 : 100, result.OutputQuantity);
            Assert.Empty(commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
        }
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        using (WorldDbContext baseline = new(checkpoint.Database))
        {
            string sql = zeroGas
                ? FormattableString.Invariant(
                    $"""
                    CREATE TRIGGER FailCompression BEFORE DELETE ON WorldItem WHEN OLD.Id={item}
                    BEGIN SELECT RAISE(ABORT,'forced compression checkpoint failure'); END;
                    """
                )
                : FormattableString.Invariant(
                    $"""
                    CREATE TRIGGER FailCompression BEFORE UPDATE ON WorldItem WHEN OLD.Id={item} AND NEW.TypeId!=OLD.TypeId
                    BEGIN SELECT RAISE(ABORT,'forced compression checkpoint failure'); END;
                    """
                );
            baseline.Database.ExecuteSqlRaw(sql);
        }
        Assert.Throws<SqliteException>(() => checkpoint.Project());
        using (WorldDbContext failed = new(checkpoint.Database))
        {
            Assert.Equal(module.NextFuelAt, failed.Set<SavedStructureService>().Find(module.ItemId)!.NextFuelAt);
            Assert.Equal(zeroGas ? gas.Compressed : Veldspar, failed.Set<OwnedItem>().Find(item)!.TypeId);
            failed.Database.ExecuteSqlRaw("DROP TRIGGER FailCompression");
        }
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Equal(
            module.NextFuelAt + StructureRules.FuelInterval,
            reopened.Set<SavedStructureService>().Find(module.ItemId)!.NextFuelAt
        );
        Assert.Equal(zeroGas ? (int?)null : resultType, reopened.Set<OwnedItem>().Find(item)?.TypeId);
    }

    [Fact]
    public void NpcStationsAndShipsInSpaceCannotBorrowAReprocessingServiceFromAnOwnedStructure()
    {
        using Fixture f = new();
        var facility = Prepare(f);
        long item = HangarItem(f, facility.Id, Veldspar, 10);
        var compression = f.Db.Services.GetRequiredService<IStructureCompression>();
        using (WorldDbContext world = new(f.Db.Path))
        {
            var ship = world.Ships.Find(f.ShipId)!;
            ship.StructureId = null;
            ship.StationId = 60015159;
            world.SaveChanges();
        }
        string before = Items(f);
        Assert.Equal(new(0, 0), compression.Efficiency(f.PilotId));
        Assert.Throws<InvalidDataException>(() => compression.Compress(f.PilotId, item));
        Assert.Equal(before, Items(f));
        f.Db.Store.SetPosition(f.ShipId, Fixture.Point, Fixture.SystemId);
        Assert.Equal(new(0, 0), compression.Efficiency(f.PilotId));
        Assert.Throws<InvalidDataException>(() => compression.Compress(f.PilotId, item));
        Assert.Equal(before, Items(f));
    }
}
