using System.Text.Json;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class SkillExtractionTestData
{
    internal const long Pilot = SkillInjectorTestData.Pilot;
    internal const int Filler = SkillInjectorTestData.Filler;

    internal static TestWorld Create(TestClock clock)
    {
        var db = SkillInjectorTestData.Create(clock, clones: true);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Add(
            new ItemGroup
            {
                Id = 1301,
                CategoryId = 5,
                Name = "Services",
            }
        );
        catalog.Add(
            new ItemType
            {
                Id = SkillExtractionRules.ExtractorType,
                GroupId = 1301,
                Name = "Skill Extractor",
            }
        );
        catalog.Add(
            new SourceRecord
            {
                Dataset = "client/types",
                Key = SkillExtractionRules.ExtractorType.ToString(),
                Data = "{\"published\":1,\"volume\":1}",
            }
        );
        catalog.SaveChanges();
        HomeStationTestData.MakeActiveCapsule(db, HomeStationTestData.Origin);
        SkillInjectorTestData.Total(db, 8_000_000);
        return db;
    }

    internal static long Item(TestWorld db, int quantity = 1) =>
        SkillInjectorTestData.Item(db, SkillExtractionRules.ExtractorType, quantity);

    internal static IPilotSkillExtraction Store(TestWorld db) => db.Services.GetRequiredService<IPilotSkillExtraction>();

    internal static SkillExtractionRequest[] Request(long points = SkillExtractionRules.Points) => [new(Filler, points)];

    internal static void Skill(TestWorld db, int type, long points)
    {
        using WorldDbContext world = new(db.Path);
        var skill = world.Set<PilotSkill>().Find(Pilot, type) ?? world.Add(new PilotSkill { PilotId = Pilot, TypeId = type }).Entity;
        skill.Points = points;
        world.SaveChanges();
    }

    internal static void Requires(TestWorld db, int type, int prerequisite, int level)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        var row =
            catalog
                .Set<SourceRecord>()
                .AsTracking()
                .SingleOrDefault(r => r.Dataset == "client/requiredskillsfortypes" && r.Key == type.ToString())
            ?? catalog.Add(new SourceRecord { Dataset = "client/requiredskillsfortypes", Key = type.ToString() }).Entity;
        row.Data = JsonSerializer.Serialize(new Dictionary<string, long> { [prerequisite.ToString()] = level });
        catalog.SaveChanges();
    }
}
