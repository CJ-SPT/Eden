using System.Text.Json;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class SkillInjectorTestData
{
    internal const long Pilot = BoosterTestData.Pilot;
    internal const int Fixed = 49704;
    internal const int Augmentor = 49979;
    internal const int Filler = 900099;

    internal static TestClock Clock() => new(new DateTimeOffset(2018, 12, 15, 12, 0, 0, TimeSpan.Zero));

    internal static TestWorld Create(TestClock clock, bool clones = false)
    {
        var db = BoosterTestData.Create(clock, clones);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        if (catalog.Set<ItemCategory>().Find(5) is null)
        {
            catalog.Add(new ItemCategory { Id = 5, Name = "Pilot services" });
        }
        catalog.Add(
            new ItemGroup
            {
                Id = SkillInjectorRules.Group,
                CategoryId = 5,
                Name = "Skill Injectors",
            }
        );
        foreach (int type in new[] { 40520, 43630, 45635, 46375, Fixed, 42523 })
        {
            catalog.Add(
                new ItemType
                {
                    Id = type,
                    GroupId = SkillInjectorRules.Group,
                    Name = $"Skill injector {type}",
                }
            );
            Add("client/types", type, new { published = type == 42523 ? 0 : 1, volume = 1 });
        }
        Attribute(43630, DogmaAttributeId.ContainedSkillPoints, 25_000);
        Attribute(43630, DogmaAttributeId.MaximumCharacterSkillPoints, 1_500_000);
        Attribute(46375, DogmaAttributeId.ContainedSkillPoints, 50_000);
        Attribute(Fixed, DogmaAttributeId.ContainedSkillPoints, 256_000);
        catalog.Add(
            new ItemType
            {
                Id = Augmentor,
                GroupId = BoosterRules.Group,
                Name = "Expired 'Absolute' Injection Augmentor",
            }
        );
        Add("client/types", Augmentor, new { published = 1, volume = 1 });
        Add("client/typedogma", Augmentor, new { dogmaEffects = Array.Empty<object>() });
        Attribute(Augmentor, DogmaAttributeId.BoosterDuration, 57_600_000);
        Attribute(Augmentor, DogmaAttributeId.BoosterSlot, 14);
        Attribute(Augmentor, DogmaAttributeId.NonDestructible, 1);
        Attribute(Augmentor, DogmaAttributeId.FollowsJumpClones, 1);
        Attribute(Augmentor, DogmaAttributeId.BoosterLastInjectionDate, 17903.4583333);
        Attribute(Augmentor, DogmaAttributeId.NonDiminishingSkillInjectorUses, 3);
        catalog.Add(
            new ItemType
            {
                Id = Filler,
                GroupId = 257,
                Name = "Ranked total-SP fixture",
            }
        );
        Attribute(Filler, DogmaAttributeId.SkillTimeConstant, 1000);
        catalog.SaveChanges();
        return db;

        void Attribute(int type, DogmaAttributeId id, double value)
        {
            if (catalog.Set<DogmaAttribute>().Find((int)id) is null && !catalog.Set<DogmaAttribute>().Local.Any(a => a.Id == (int)id))
            {
                catalog.Add(new DogmaAttribute { Id = (int)id, Name = $"injector-{id}" });
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
        void Add(string dataset, int key, object data) =>
            catalog.Add(
                new SourceRecord
                {
                    Dataset = dataset,
                    Key = key.ToString(),
                    Data = JsonSerializer.Serialize(data),
                }
            );
    }

    internal static IPilotSkillInjectors Store(TestWorld db) => db.Services.GetRequiredService<IPilotSkillInjectors>();

    internal static long Item(TestWorld db, int type = (int)SkillInjectorType.Large, int quantity = 1) =>
        ImplantTestData.Item(db, type, quantity);

    internal static void Total(TestWorld db, long total, long free = 0, AccountCloneGrade grade = AccountCloneGrade.Omega)
    {
        using WorldDbContext world = new(db.Path);
        foreach (var skill in world.Set<PilotSkill>().Where(s => s.PilotId == Pilot))
        {
            skill.Points = 0;
        }
        var filler = world.Set<PilotSkill>().Find(Pilot, Filler) ?? world.Add(new PilotSkill { PilotId = Pilot, TypeId = Filler }).Entity;
        filler.Points = total - free;
        var pool = world.Set<SavedFreeSkillPoints>().Find(Pilot) ?? world.Add(new SavedFreeSkillPoints { PilotId = Pilot }).Entity;
        pool.Points = free;
        world.Set<Account>().Find(1000001L)!.CloneGrade = grade;
        world.SaveChanges();
    }
}
