using Eden.Gameplay.Docking;
using Eden.Gameplay.Skills;
using Eden.World.Docking;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class WorldTests
{
    [Fact]
    public void EachPilotRetainsIndependentSkillProgressAfterReopen()
    {
        using TestWorld db = new();
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<PilotSkill>().Find(90000002L, 3327)!.Points = 1415;
            world.SaveChanges();
        }
        _ = WorldInitialization.Open(db.Path);
        var reader = db.Services.GetRequiredService<IPilotSkillReader>();
        var pilot = Assert.Single(reader.ReadSkills(90000001));
        var wing = Assert.Single(reader.ReadSkills(90000002));
        Assert.Equal(new TrainedSkill(3327, 8000, 1), pilot);
        Assert.Equal(3, pilot.Level);
        Assert.Equal(1415, wing.Points);
        Assert.Equal(2, wing.Level);
        Assert.Equal(1, (wing with { Points = 1414 }).Level);
        Assert.Empty(reader.ReadSkills(90000003));
    }

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
        command.CommandText = "UPDATE WorldPilot SET name='Persisted' WHERE id=90000001";
        Assert.Equal(1, command.ExecuteNonQuery());
        SqliteWorld reopened = WorldInitialization.Open(db.Path);
        Assert.Equal(pilot with { Name = "Persisted" }, reopened.FindPilot(1000001));
        Assert.Equal(1000000000002L, reopened.FindPilot(1000002)!.ShipId);
        Assert.Null(reopened.FindPilot(1000003));
        command.CommandText = "SELECT count(*) FROM WorldShip WHERE OwnerId=90000001";
        Assert.Equal(1L, command.ExecuteScalar());
    }
}
