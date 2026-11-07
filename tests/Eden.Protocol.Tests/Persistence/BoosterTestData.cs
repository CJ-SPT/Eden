using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class BoosterTestData
{
    internal const long Pilot = ImplantTestData.Pilot;
    internal const int Master = 48582;
    internal const int Basic = 72238;
    internal const int Science = 3402;

    // Pinned build 3662168 client/types and typedogma: all published profiles
    // with only the five learning effects and uniform attribute bonuses.
    internal static readonly (
        int Type,
        int Slot,
        int Bonus,
        long DurationMs,
        int AgeHours,
        double? LastDate,
        bool RequiresScience
    )[] Profiles =
    [
        (2838, 4, 5, 0, 840, null, true),
        (33087, 4, 17, 0, 168, null, true),
        (33111, 4, 9, 0, 336, null, true),
        (47265, 10, 10, 86400000, 0, 17610.4583333, true),
        (47266, 10, 12, 86400000, 0, 17610.4583333, true),
        (47267, 10, 10, 259200000, 0, 17610.4583333, true),
        (47395, 10, 10, 86400000, 0, 17645.4583333, true),
        (47396, 10, 12, 86400000, 0, 17645.4583333, true),
        (47397, 10, 10, 259200000, 0, 17645.4583333, true),
        (47481, 10, 12, 259200000, 0, 17652.4583333, true),
        (48582, 10, 10, 86400000, 0, null, true),
        (49753, 10, 10, 259200000, 0, 17862.4583333, true),
        (55826, 10, 8, 1080000000, 0, null, true),
        (55927, 19, 5, 43200000, 0, 18506.66597222222, true),
        (55928, 51, 5, 43200000, 0, 18522.66597222222, true),
        (55929, 52, 5, 86400000, 0, 18522.66597222222, true),
        (55942, 25, 1, 60000, 2400, null, true),
        (55943, 26, 1, 60000, 2400, null, true),
        (55944, 27, 1, 60000, 2400, null, true),
        (55945, 28, 1, 60000, 2400, null, true),
        (55946, 29, 1, 60000, 2400, null, true),
        (55947, 30, 1, 60000, 2400, null, true),
        (55977, 53, 1, 2937600000, 0, 18503.125, true),
        (56128, 19, 5, 43200000, 0, 18536.66597222222, true),
        (56129, 54, 5, 43200000, 0, 18543.125, true),
        (56130, 55, 5, 86400000, 0, 18543.125, true),
        (56179, 56, 1, 2592000000, 0, 18531.125, true),
        (56180, 38, 1, 60000, 4320, null, true),
        (56181, 39, 1, 60000, 4320, null, true),
        (56182, 40, 1, 60000, 4320, null, true),
        (56183, 41, 1, 60000, 4320, null, true),
        (56184, 42, 1, 60000, 4320, null, true),
        (56185, 43, 1, 60000, 4320, null, true),
        (56186, 44, 1, 60000, 0, null, true),
        (56187, 45, 1, 60000, 0, null, true),
        (56619, 19, 5, 43200000, 0, 18567.66597222222, true),
        (56620, 57, 5, 43200000, 0, 18576.125, true),
        (56621, 58, 5, 86400000, 0, 18576.125, true),
        (56640, 59, 1, 2764800000, 0, 18564.125, true),
        (56659, 60, 3, 604800000, 0, 18555.125, true),
        (56661, 61, 10, 604800000, 0, 18555.125, true),
        (56662, 62, 15, 604800000, 0, 18555.125, true),
        (56823, 19, 5, 43200000, 0, 18597.66597222222, true),
        (56833, 63, 5, 43200000, 0, 18606.125, true),
        (56834, 64, 5, 86400000, 0, 18606.125, true),
        (56835, 66, 1, 2419200000, 0, 18594.125, true),
        (56893, 34, 1, 60000, 0, 18627.66597222222, false),
        (56970, 19, 5, 43200000, 0, 18628.66597222222, true),
        (56984, 67, 1, 60000, 6480, null, true),
        (56985, 68, 1, 60000, 6480, null, true),
        (56986, 69, 1, 60000, 6480, null, true),
        (56987, 70, 1, 60000, 6480, null, true),
        (56988, 71, 1, 60000, 6480, null, true),
        (56989, 72, 1, 60000, 6480, null, true),
        (56990, 73, 1, 60000, 6480, null, true),
        (56991, 74, 1, 60000, 6480, null, true),
        (56992, 75, 1, 60000, 6480, null, true),
        (56993, 76, 1, 60000, 6480, null, true),
        (56994, 77, 1, 60000, 6480, null, true),
        (56995, 78, 1, 60000, 6480, null, true),
        (57035, 79, 5, 43200000, 0, 18637.125, true),
        (57036, 80, 5, 86400000, 0, 18637.125, true),
        (57037, 81, 1, 2505600000, 0, 18625.125, true),
        (57201, 19, 5, 43200000, 0, 18659.66597222222, true),
        (57209, 82, 5, 43200000, 0, 18668.125, true),
        (57210, 83, 5, 86400000, 0, 18668.125, true),
        (57211, 84, 1, 2505600000, 0, 18656.125, true),
        (57242, 86, 1, 86400000, 0, null, false),
        (57243, 86, 1, 259200000, 0, null, false),
        (57244, 86, 1, 604800000, 0, null, false),
        (57245, 87, 3, 86400000, 0, null, false),
        (57246, 87, 3, 259200000, 0, null, false),
        (57247, 87, 3, 604800000, 0, null, false),
        (57248, 88, 5, 86400000, 0, null, false),
        (57249, 88, 5, 259200000, 0, null, false),
        (57250, 88, 5, 604800000, 0, null, false),
        (57251, 91, 10, 86400000, 0, null, false),
        (57252, 91, 10, 259200000, 0, null, false),
        (57253, 91, 10, 604800000, 0, null, false),
        (57287, 92, 1, 60000, 8640, null, true),
        (57288, 93, 1, 60000, 8640, null, true),
        (57289, 94, 1, 60000, 8640, null, true),
        (57290, 95, 1, 60000, 8640, null, true),
        (57291, 96, 1, 60000, 8640, null, true),
        (57292, 97, 1, 60000, 8640, null, true),
        (57293, 98, 1, 60000, 8640, null, true),
        (57294, 99, 1, 60000, 8640, null, true),
        (57295, 19, 5, 43200000, 0, 18687.66597222222, true),
        (57296, 100, 2, 86400000, 0, 18717.66597222222, false),
        (57297, 101, 4, 86400000, 0, 18717.66597222222, false),
        (57298, 102, 6, 86400000, 0, 18717.66597222222, false),
        (57299, 103, 8, 86400000, 0, 18717.66597222222, false),
        (57300, 104, 2021, 1200000, 0, 18717.66597222222, false),
        (57301, 104, 2021, 1260000, 0, 18717.66597222222, false),
        (57311, 105, 5, 43200000, 0, 18696.125, true),
        (57312, 106, 5, 86400000, 0, 18696.125, true),
        (57313, 107, 1, 2246400000, 0, 18684.125, true),
        (57323, 108, 10, 86400000, 0, null, false),
        (57324, 108, 10, 259200000, 0, null, false),
        (57325, 108, 10, 432000000, 0, null, false),
        (57326, 108, 10, 604800000, 0, null, false),
        (57428, 19, 5, 43200000, 0, 18718.66597222222, true),
        (57430, 112, 5, 43200000, 0, 18727.125, true),
        (57431, 113, 5, 86400000, 0, 18727.125, true),
        (57432, 114, 1, 2505600000, 0, 18715.125, true),
        (57722, 115, 1, 60000, 8640, null, true),
        (57723, 116, 1, 60000, 8640, null, true),
        (57724, 117, 1, 60000, 8640, null, true),
        (57725, 118, 1, 60000, 8640, null, true),
        (57726, 119, 1, 60000, 8640, null, true),
        (57727, 120, 1, 60000, 8640, null, true),
        (57728, 121, 1, 60000, 8640, null, true),
        (57763, 19, 5, 43200000, 0, 18748.66597222222, true),
        (57765, 122, 10, 86400000, 0, null, false),
        (57766, 122, 10, 259200000, 0, null, false),
        (57767, 122, 10, 432000000, 0, null, false),
        (57768, 122, 10, 604800000, 0, null, false),
        (57821, 124, 5, 43200000, 0, 18757.125, true),
        (57822, 125, 5, 86400000, 0, 18757.125, true),
        (57823, 126, 1, 432000000, 0, 18744.125, true),
        (58727, 19, 5, 43200000, 0, 18779.66597222222, true),
        (58758, 127, 5, 43200000, 0, 18788.125, true),
        (58759, 128, 5, 86400000, 0, 18788.125, true),
        (58760, 129, 1, 2505600000, 0, 18776.125, true),
        (58770, 130, 1, 10000, 0, 18753.125, true),
        (58771, 130, 1, 10000, 0, 18753.125, true),
        (58772, 130, 1, 10000, 0, 18753.125, true),
        (59177, 19, 5, 43200000, 0, 18809.66597222222, true),
        (59294, 131, 5, 43200000, 0, 18818.125, true),
        (59295, 132, 5, 86400000, 0, 18818.125, true),
        (59296, 133, 1, 2419200000, 0, 18806.125, true),
        (59297, 134, 1, 10000, 0, 18793.125, true),
        (59298, 134, 1, 10000, 0, 18793.125, true),
        (59299, 134, 1, 10000, 0, 18793.125, true),
        (59301, 10, 6, 259200000, 0, 18808.125, true),
        (59302, 10, 8, 86400000, 0, 18808.125, true),
        (59303, 10, 4, 86400000, 0, 18808.125, true),
        (59620, 19, 5, 43200000, 0, 18840.66597222222, true),
        (59756, 181, 5, 86400000, 0, 18849.125, true),
        (59757, 182, 1, 1728000000, 0, 18836.125, true),
        (59758, 183, 1, 60000, 0, null, true),
        (59759, 184, 1, 60000, 0, null, true),
        (59760, 185, 1, 60000, 0, null, true),
        (59761, 186, 1, 60000, 0, null, true),
        (59762, 187, 1, 60000, 0, null, true),
        (59763, 188, 1, 60000, 0, null, true),
        (59764, 189, 1, 60000, 0, null, true),
        (59765, 190, 1, 60000, 0, null, true),
        (59766, 191, 1, 60000, 0, null, true),
        (59767, 192, 1, 60000, 0, null, true),
        (59768, 193, 1, 60000, 0, null, true),
        (59803, 196, 10, 259200000, 0, null, false),
        (59804, 196, 10, 432000000, 0, null, false),
        (59805, 196, 10, 604800000, 0, null, false),
        (59806, 196, 10, 1209600000, 0, null, false),
        (59960, 197, 1, 60000, 0, null, true),
        (59961, 198, 1, 60000, 0, null, true),
        (59962, 199, 1, 60000, 0, null, true),
        (60023, 19, 5, 43200000, 0, 18871.66597222222, true),
        (60091, 202, 5, 43200000, 0, 18880.125, true),
        (60092, 203, 5, 86400000, 0, 18880.125, true),
        (60093, 204, 1, 1641600000, 0, 18867.125, true),
        (60172, 19, 5, 43200000, 0, 18901.66597222222, true),
        (60187, 207, 10, 86400000, 0, null, false),
        (60188, 207, 10, 259200000, 0, null, false),
        (60189, 207, 10, 432000000, 0, null, false),
        (60190, 207, 10, 604800000, 0, null, false),
        (60250, 208, 5, 86400000, 0, 18907.125, true),
        (60251, 209, 1, 1641600000, 0, 18891.125, true),
        (60269, 210, 1, 60000, 0, 18910.125, true),
        (60270, 211, 1, 60000, 0, 18910.125, true),
        (60271, 212, 1, 60000, 0, 18910.125, true),
        (60272, 213, 1, 60000, 0, 18910.125, true),
        (60273, 214, 1, 60000, 0, 18910.125, true),
        (60485, 19, 5, 43200000, 0, 18932.66597222222, true),
        (72238, 10, 2, 172800000, 0, null, false),
        (72239, 10, 4, 345600000, 0, null, false),
        (72240, 10, 6, 518400000, 0, null, false),
        (72241, 10, 8, 691200000, 0, null, false),
        (72242, 10, 10, 864000000, 0, null, false),
        (77919, 10, 12, 1036800000, 0, null, false),
        (77934, 10, 6, 172800000, 0, null, false),
        (85279, 10, 13, 259200000, 0, null, false),
        (96542, 10, 4, 86400000, 0, 20767.45763888889, true),
        (96543, 10, 8, 86400000, 0, 20767.45763888889, true),
        (96544, 10, 6, 259200000, 0, 20767.45763888889, true),
    ];

    internal static TestWorld Create(TestClock clock, bool clones = false, params int[] types)
    {
        var db = ImplantTestData.Create(clock, clones);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Add(
            new ItemGroup
            {
                Id = BoosterRules.Group,
                CategoryId = 20,
                Name = "Booster",
            }
        );
        foreach (int skill in new[] { Science, BoosterRules.BiologySkill })
        {
            catalog.Add(
                new ItemType
                {
                    Id = skill,
                    GroupId = 257,
                    Name = $"Science skill {skill}",
                }
            );
            Attribute(skill, DogmaAttributeId.SkillTimeConstant, 1);
        }
        foreach (int type in (types.Length == 0 ? new[] { Master, Basic } : types).Distinct())
        {
            var profile = Profiles.Single(p => p.Type == type);
            catalog.Add(
                new ItemType
                {
                    Id = type,
                    GroupId = BoosterRules.Group,
                    Name = $"Cerebral accelerator {type}",
                }
            );
            foreach (
                var bonus in new[]
                {
                    DogmaAttributeId.CharismaBonus,
                    DogmaAttributeId.IntelligenceBonus,
                    DogmaAttributeId.MemoryBonus,
                    DogmaAttributeId.PerceptionBonus,
                    DogmaAttributeId.WillpowerBonus,
                }
            )
            {
                Attribute(type, bonus, profile.Bonus);
            }
            Attribute(type, DogmaAttributeId.BoosterSlot, profile.Slot);
            Attribute(type, DogmaAttributeId.BoosterDuration, profile.DurationMs);
            Attribute(type, DogmaAttributeId.BoosterMaximumAgeHours, profile.AgeHours);
            Attribute(type, DogmaAttributeId.BoosterLastInjectionDate, profile.LastDate ?? 0);
            Attribute(type, DogmaAttributeId.NonDestructible, 1);
            Attribute(type, DogmaAttributeId.FollowsJumpClones, 1);
            if (profile.RequiresScience)
            {
                Attribute(type, DogmaAttributeId.RequiredSkill1, Science);
                Attribute(type, DogmaAttributeId.RequiredSkill1Level, 1);
            }
            Add("client/types", type.ToString(), new { published = 1, volume = 1 });
            Add(
                "client/typedogma",
                type.ToString(),
                new
                {
                    dogmaEffects = new[] { 302, 304, 306, 308, 310 }.Select(effect => new { effectID = effect, isDefault = 0 }).ToArray(),
                }
            );
        }
        var alpha = catalog.Set<SourceRecord>().AsTracking().Single(r => r.Dataset == "client/clonegrades" && r.Key == "\"1\"");
        alpha.Data = JsonSerializer.Serialize(
            new
            {
                skills = new[]
                {
                    new { typeID = 3327, level = 4 },
                    new { typeID = 3411, level = 3 },
                    new { typeID = Science, level = 4 },
                    new { typeID = BoosterRules.BiologySkill, level = 3 },
                },
            }
        );
        catalog.SaveChanges();
        using WorldDbContext world = new(db.Path);
        world.Pilots.Find(Pilot)!.CreatedAt = clock.Now - TimeSpan.TicksPerHour;
        world.Add(
            new PilotSkill
            {
                PilotId = Pilot,
                TypeId = Science,
                Points = 250,
            }
        );
        world.Add(
            new PilotSkill
            {
                PilotId = Pilot,
                TypeId = BoosterRules.BiologySkill,
                Points = TrainedSkill.PointsForLevel(1, 5),
            }
        );
        world.SaveChanges();
        return db;

        void Attribute(int type, DogmaAttributeId id, double value)
        {
            if (catalog.Set<DogmaAttribute>().Find((int)id) is null && !catalog.Set<DogmaAttribute>().Local.Any(a => a.Id == (int)id))
            {
                catalog.Add(new DogmaAttribute { Id = (int)id, Name = $"booster-{id}" });
            }
            catalog.Add(
                new TypeAttribute
                {
                    ItemTypeId = type,
                    AttributeId = (int)id,
                    Value = value,
                }
            );
        }
        void Add(string dataset, string key, object data) =>
            catalog.Add(
                new SourceRecord
                {
                    Dataset = dataset,
                    Key = key,
                    Data = JsonSerializer.Serialize(data),
                }
            );
    }

    internal static IPilotBoosters Store(TestWorld db) => db.Services.GetRequiredService<IPilotBoosters>();

    internal static long Item(TestWorld db, int type = Master, int quantity = 1) => ImplantTestData.Item(db, type, quantity);

    internal static BoosterChange Consume(TestWorld db, long id) => Store(db).Consume(Pilot, id, HomeStationTestData.Origin);

    internal static void ShortLifetime(TestWorld db, int seconds = 120, int type = Master)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog
            .Set<TypeAttribute>()
            .AsTracking()
            .Single(a => a.ItemTypeId == type && a.AttributeId == (int)DogmaAttributeId.BoosterDuration)
            .Value = seconds * 1000;
        catalog.SaveChanges();
        using WorldDbContext world = new(db.Path);
        world.Set<PilotSkill>().Find(Pilot, BoosterRules.BiologySkill)!.Points = 0;
        world.SaveChanges();
    }
}
