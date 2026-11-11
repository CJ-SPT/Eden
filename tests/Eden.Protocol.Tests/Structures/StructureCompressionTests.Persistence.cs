using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Structures;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed partial class StructureCompressionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedConversionRollsBackFuelAtItsDeadlineAndTheStackThenRetryCommitsOnce(bool liveWorld, bool zeroGas)
    {
        using Fixture f = new(liveWorld: liveWorld);
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
        LiveWorld.Find(f.Db.Path)?.Dispose();
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
