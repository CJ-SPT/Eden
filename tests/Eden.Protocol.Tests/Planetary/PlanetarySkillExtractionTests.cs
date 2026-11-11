using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Data.World.Planetary;
using Eden.Data.World.Skills;
using Eden.Gameplay.Planetary;
using Eden.Gameplay.Skills;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Planetary;

public sealed class PlanetarySkillExtractionTests
{
    [Theory]
    [InlineData(PlanetaryRules.CommandCenterUpgradesSkill, 1, 5)]
    [InlineData(PlanetaryRules.InterplanetaryConsolidationSkill, 6, 0)]
    public void ExtractionCannotRemoveLevelsRequiredByExistingColonies(int skill, int colonies, int level)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        PassiveCatalogFixture.LoadPlanetary(new WorldDatabase(db.Path).CatalogPath);
        long points = TrainedSkill.PointsForLevel(4, 5);
        SkillExtractionTestData.Skill(db, skill, points);
        long item = SkillExtractionTestData.Item(db);
        using (WorldDbContext world = new(db.Path))
        {
            for (int index = 0; index < colonies; index++)
            {
                int planet = PlanetaryIndustryTests.PlanetId + index;
                var colony = PlanetaryRules.Empty(SkillExtractionTestData.Pilot, planet, clock.Now) with { Level = level };
                world.Add(
                    new SavedPlanetaryColony
                    {
                        PilotId = colony.OwnerId,
                        PlanetId = planet,
                        StateJson = JsonSerializer.Serialize(colony),
                    }
                );
            }
            world.SaveChanges();
        }
        Assert.Throws<InvalidSkillExtraction>(() =>
            SkillExtractionTestData.Store(db).Extract(SkillExtractionTestData.Pilot, item, [new(skill, SkillExtractionRules.Points)])
        );
        using WorldDbContext unchanged = new(db.Path);
        Assert.Equal(points, unchanged.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, skill)!.Points);
        Assert.Equal(1, unchanged.Set<OwnedItem>().Find(item)!.Quantity);
        Assert.Empty(unchanged.Set<SavedSkillExtraction>());
    }
}
