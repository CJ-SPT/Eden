using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;

namespace Eden.Protocol.Tests.Skills;

public sealed partial class ExpertSystemTests
{
    private static void LoadCatalog(string path, bool includeLimitSystems)
    {
        PassiveCatalogFixture.LoadUtilityHulls(path);
        using CatalogDbContext catalog = new(path, writable: true);
        catalog.Add(new ItemCategory { Id = (int)InventoryCategory.ExpertSystem, Name = "Expert Systems" });
        const int StandardExpertSystems = 4091;
        catalog.Add(
            new ItemGroup
            {
                Id = StandardExpertSystems,
                CategoryId = (int)InventoryCategory.ExpertSystem,
                Name = "Standard Expert Systems",
            }
        );

        // Build 3662168 client/expertsystems entry 57207. UtilityHullCatalog supplies the ship, module, and their required skills.
        AddSystem(
            MiningBargeOperations,
            "Mining Barge Operations",
            """
            {"internalName":"Mining_Barge","esHidden":0,"durationDays":7,
            "skillsGranted":{"23566":3,"12305":3,"17940":4,"16281":3,"24241":3,"22578":3,"23606":3,"3386":4,
            "3405":3,"3410":3,"3416":3,"3418":4,"3419":3,"3420":3,"3424":3,"3425":3,"3428":3,"3431":3,"32999":3,
            "3436":3,"3437":3,"3438":3,"3442":3,"3449":4,"3450":4,"3455":3,"3453":3,"3327":4},
            "associatedShipTypes":[17478,17476,17480],"esRetired":0}
            """
        );
        (int Id, string Name, int Group, string GroupName, double Rank)[] supplementalSkills =
        [
            (3405, "Biology", 1220, "Neural Enhancement", 1),
            (3418, "Capacitor Management", 1216, "Engineering", 3),
            (3419, "Shield Management", 1209, "Shields", 3),
            (3420, "Tactical Shield Manipulation", 1209, "Shields", 4),
            (3424, "Energy Grid Upgrades", 1216, "Engineering", 2),
            (3428, "Long Range Targeting", 1213, "Targeting", 2),
            (3431, "Signature Analysis", 1213, "Targeting", 1),
            (3437, "Drone Avionics", 273, "Drones", 1),
            (3438, "Mining Drone Operation", 273, "Drones", 2),
            (3453, "Evasive Maneuvering", 275, "Navigation", 2),
            (3455, "Warp Drive Operation", 275, "Navigation", 1),
            (12305, "Drone Navigation", 273, "Drones", 1),
            (22578, "Mining Upgrades", 1218, "Resource Processing", 4),
            (23566, "Advanced Drone Avionics", 273, "Drones", 5),
            (23606, "Drone Sharpshooting", 273, "Drones", 1),
            (32999, "Magnetometric Sensor Compensation", 1213, "Targeting", 2),
        ];
        foreach (var skill in supplementalSkills)
        {
            if (catalog.Set<ItemGroup>().Find(skill.Group) is null)
            {
                catalog.Add(
                    new ItemGroup
                    {
                        Id = skill.Group,
                        Name = skill.GroupName,
                        CategoryId = (int)InventoryCategory.Skill,
                    }
                );
            }
            catalog.Add(
                new ItemType
                {
                    Id = skill.Id,
                    Name = skill.Name,
                    GroupId = skill.Group,
                }
            );
            catalog.Add(
                new TypeAttribute
                {
                    ItemTypeId = skill.Id,
                    AttributeId = (int)DogmaAttributeId.SkillTimeConstant,
                    Value = skill.Rank,
                }
            );
        }

        if (includeLimitSystems)
        {
            // These three pinned systems share the Spaceship Command IV grant. The slot test needs only that common grant.
            AddSystem(CoreShipOperations, "Core Ship Operations", """{"durationDays":7,"skillsGranted":{"3327":4}}""");
            AddSystem(SpaceExploration, "Space Exploration", """{"durationDays":7,"skillsGranted":{"3327":4}}""");
            AddSystem(FactionWarfare, "Faction Warfare", """{"durationDays":7,"skillsGranted":{"3327":4}}""");
        }
        catalog.SaveChanges();

        void AddSystem(int type, string name, string data)
        {
            string key = type.ToString(CultureInfo.InvariantCulture);
            catalog.Add(
                new ItemType
                {
                    Id = type,
                    Name = name,
                    GroupId = StandardExpertSystems,
                }
            );
            catalog.Add(
                new SourceRecord
                {
                    Dataset = CatalogData.Client.Expertsystems.Name,
                    Key = key,
                    Data = data,
                }
            );
            catalog.Add(
                new SourceRecord
                {
                    Dataset = CatalogData.Client.Types.Name,
                    Key = key,
                    Data = """{"volume":1.0,"published":1}""",
                }
            );
        }
    }
}
