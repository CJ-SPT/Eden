using Eden.Protocol.Docking;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Eden.Protocol.Tests.Persistence;

public sealed class WorldTests
{
    [Fact]
    public void ReopeningWorldKeepsExactlyOneOwnedActiveShip()
    {
        using TestWorld db = new();
        DockedPilot pilot = Assert.IsType<DockedPilot>(db.Store.FindPilot(1000001));
        Assert.Equal(601, pilot.ShipTypeId);
        Assert.Equal(60003760, pilot.StationId);
        Assert.NotEqual(pilot.ShipTypeId, pilot.ShipId);
        using var connection = new SqliteConnection($"Data Source={db.Path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE pilot SET name='Persisted' WHERE id=90000001";
        Assert.Equal(1, command.ExecuteNonQuery());
        SqliteWorld reopened = new(db.Path);
        Assert.Equal(pilot with { Name = "Persisted" }, reopened.FindPilot(1000001));
        Assert.Equal(1000000000002L, reopened.FindPilot(1000002)!.ShipId);
        Assert.Null(reopened.FindPilot(1000003));
        command.CommandText = "SELECT count(*) FROM ship WHERE owner=90000001";
        Assert.Equal(1L, command.ExecuteScalar());
    }
}
