using Eden.Gameplay.Characters;
using Eden.Protocol.Characters;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CharacterCreationTests
{
    [Theory]
    [InlineData(11, 4, 5, 30003489, 60015154, 596)]
    [InlineData(12, 4, 5, 30005042, 60015155, 596)]
    [InlineData(13, 4, 5, 30001672, 60015156, 596)]
    [InlineData(14, 2, 3, 30002547, 60015162, 588)]
    [InlineData(15, 2, 3, 30002505, 60015164, 588)]
    [InlineData(16, 2, 3, 30003410, 60015163, 588)]
    [InlineData(17, 1, 1, 30000141, 60015157, 601)]
    [InlineData(18, 1, 1, 30001392, 60015159, 601)]
    [InlineData(19, 1, 1, 30001407, 60015158, 601)]
    [InlineData(20, 8, 7, 30004971, 60015160, 606)]
    [InlineData(21, 8, 7, 30002715, 60015153, 606)]
    [InlineData(22, 8, 7, 30005305, 60015161, 606)]
    [InlineData(31, 4, 5, 30000141, 60015157, 596)]
    public void SchoolDeterminesStationAndRaceDeterminesShip(int school, int race, int bloodline, int system, int station, int hull)
    {
        using TestWorld db = new();
        TestCatalog.AddSchool(new WorldDatabase(db.Path).CatalogPath, school, race, bloodline, system, station, hull, school == 31);
        var creation = db.Services.GetRequiredService<ICharacterCreation>();
        var character = new NewCharacter("New Pilot", race, bloodline, 0, 64, [1, 2], [3, 4], school);
        long id = creation.Create(1000001, character);
        var reopened = WorldInitialization.Open(db.Path);
        var pilot = Assert.Single(reopened.ListPilots(1000001), p => p.PilotId == id);
        Assert.Equal((station, system, hull, 1000044), (pilot.StationId, pilot.SystemId, pilot.ShipTypeId, pilot.CorpId));
        Assert.Null(pilot.Position);
        Assert.Equal(
            (race, bloodline, 0, 64, school),
            (pilot.Profile.RaceId, pilot.Profile.BloodlineId, pilot.Profile.GenderId, pilot.Profile.AncestryId, pilot.Profile.SchoolId)
        );
        Assert.Equal(character.Appearance, creation.ReadAppearance(id, false));
        Assert.Equal(character.Portrait, creation.ReadAppearance(id, true));
        using WorldDbContext save = new(db.Path);
        Assert.Equal(8000, save.Set<PilotSkill>().Single(s => s.PilotId == id).Points);
    }

    [Fact]
    public void SlotsAndNamesAreCheckedInsideTheCreationTransaction()
    {
        using TestWorld db = new();
        TestCatalog.AddSchool(new WorldDatabase(db.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        var creation = db.Services.GetRequiredService<ICharacterCreation>();
        NewCharacter character = new("Amsen Pilot", 1, 1, 1, 64, [1], [2], 18);
        creation.Create(1000001, character);
        Assert.Equal(CharacterNameResult.Taken, creation.ValidateName("amsen pilot"));
        Assert.Throws<InvalidDataException>(() => creation.Create(1000002, character with { Name = "AMSEN PILOT" }));
        Assert.Throws<InvalidDataException>(() => creation.Create(1000001, character));
        Assert.Throws<InvalidDataException>(() => creation.Create(1000001, character with { Name = "Unknown School", SchoolId = 99 }));
        creation.Create(1000001, character with { Name = "Third Pilot" });
        Assert.Throws<InvalidDataException>(() => creation.Create(1000001, character with { Name = "Fourth Pilot" }));
        using WorldDbContext save = new(db.Path);
        Assert.Equal(4, save.Pilots.Count());
        Assert.Equal(4, save.Ships.Count());
        Assert.Equal(4, save.Set<PilotSkill>().Count());
    }

    [Fact]
    public void FailureAfterShipInsertRollsBackEntireCreation()
    {
        using TestWorld db = new();
        TestCatalog.AddSchool(new WorldDatabase(db.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        using WorldDbContext save = new(db.Path);
        save.Database.ExecuteSqlRaw(
            "CREATE TRIGGER reject_skill BEFORE INSERT ON WorldPilotSkill BEGIN SELECT RAISE(ABORT,'test rollback'); END"
        );
        var creation = db.Services.GetRequiredService<ICharacterCreation>();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            creation.Create(1000001, new("Rollback Pilot", 1, 1, 1, 64, [1], [2], 18))
        );
        Assert.Equal(2, save.Pilots.Count());
        Assert.Equal(2, save.Ships.Count());
        Assert.Equal(2, save.Set<PilotSkill>().Count());
    }
}
