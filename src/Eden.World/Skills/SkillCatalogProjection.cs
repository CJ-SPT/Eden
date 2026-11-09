using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Microsoft.Data.Sqlite;

namespace Eden.World.Skills;

// This adapter owns the dynamic client-data documents at the package/storage boundary.
public static class SkillCatalogProjection
{
    private const int FirstMessageId = 1_000_000_000;

    public static string Create(string source, string destination, SkillEditorSnapshot snapshot)
    {
        using SqliteConnection original = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = source,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        using SqliteConnection target = new(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        original.Open();
        target.Open();
        original.BackupDatabase(target);
        using var transaction = target.BeginTransaction();
        JsonObject types = new();
        JsonObject dogma = new();
        JsonObject required = new();
        JsonObject messages = new();
        JsonObject alpha = new();
        JsonObject indices = new();
        JsonObject templates = new();
        var changes = snapshot
            .Skills.Where(entry => entry.UserCreated || entry.IsModified)
            .OrderBy(entry => entry.Definition.TypeId)
            .ToArray();
        int messageId = FirstMessageId;
        foreach (var entry in changes)
        {
            var skill = entry.Definition;
            string key = skill.TypeId.ToString(CultureInfo.InvariantCulture);
            int template = snapshot.Skills.First(value => value.Original?.GroupId == skill.GroupId).Definition.TypeId;
            JsonObject type =
                Read("client/types", key)
                ?? Read("client/types", template.ToString(CultureInfo.InvariantCulture))
                ?? new()
                {
                    ["volume"] = 0.01,
                    ["portionSize"] = 1,
                    ["mass"] = 0,
                    ["radius"] = 1,
                    ["capacity"] = 0,
                    ["isDynamicType"] = 0,
                };
            type["typeID"] = skill.TypeId;
            templates[key] = template;
            type["typeNameID"] = messageId++;
            type["descriptionID"] = messageId++;
            foreach (var pair in new[] { (Id: messageId - 2, Text: skill.Name), (Id: messageId - 1, Text: skill.Description) })
            {
                string messageKey = pair.Id.ToString(CultureInfo.InvariantCulture);
                if (Read("client/localization_fsd_en-us", messageKey) is not null)
                {
                    throw new InvalidDataException("The reserved skill message range overlaps the original catalog.");
                }
                messages[messageKey] = pair.Text;
                Write("client/localization_fsd_en-us", messageKey, new JsonObject { ["language"] = "en-us", ["text"] = pair.Text });
            }
            type["groupID"] = skill.GroupId;
            type["iconID"] = skill.IconId;
            type["marketGroupID"] = skill.MarketGroupId;
            type["published"] = !entry.Deleted && skill.Published ? 1 : 0;
            type["basePrice"] = skill.BasePrice;
            types[key] = type.DeepClone();
            Write("client/types", key, type);
            Execute(
                "INSERT INTO ItemType(Id,Name,GroupId,Mass,Radius,Capacity) VALUES($id,$name,$group,0,1,0) "
                    + "ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,GroupId=excluded.GroupId",
                ("$id", skill.TypeId),
                ("$name", skill.Name),
                ("$group", skill.GroupId)
            );
            JsonArray attributes = new(
                skill
                    .Attributes.Select(value => (JsonNode)new JsonObject { ["attributeID"] = value.AttributeId, ["value"] = value.Value })
                    .ToArray()
            );
            attributes.Add(new JsonObject { ["attributeID"] = (int)DogmaAttributeId.PrimaryAttribute, ["value"] = skill.PrimaryAttribute });
            attributes.Add(
                new JsonObject { ["attributeID"] = (int)DogmaAttributeId.SecondaryAttribute, ["value"] = skill.SecondaryAttribute }
            );
            attributes.Add(new JsonObject { ["attributeID"] = (int)DogmaAttributeId.SkillTimeConstant, ["value"] = skill.Rank });
            JsonObject prerequisites = new();
            for (int index = 0; index < skill.Prerequisites.Length; index++)
            {
                var prerequisite = skill.Prerequisites[index];
                var ids = InventoryDefinitions.RequiredSkills[index];
                attributes.Add(new JsonObject { ["attributeID"] = (int)ids.Skill, ["value"] = prerequisite.TypeId });
                attributes.Add(new JsonObject { ["attributeID"] = (int)ids.Level, ["value"] = prerequisite.Level });
                prerequisites[prerequisite.TypeId.ToString(CultureInfo.InvariantCulture)] = prerequisite.Level;
            }
            JsonObject profile = new()
            {
                ["dogmaAttributes"] = attributes,
                ["dogmaEffects"] = new JsonArray(
                    (entry.Deleted ? [] : skill.Effects)
                        .Select(effect =>
                            (JsonNode)new JsonObject { ["effectID"] = effect.EffectId, ["isDefault"] = effect.IsDefault ? 1 : 0 }
                        )
                        .ToArray()
                ),
            };
            dogma[key] = profile.DeepClone();
            required[key] = prerequisites.DeepClone();
            Write("client/typedogma", key, profile);
            Write("client/requiredskillsfortypes", key, prerequisites);
            Execute("DELETE FROM TypeAttribute WHERE ItemTypeId=$id", ("$id", skill.TypeId));
            foreach (var attribute in attributes)
            {
                Execute(
                    "INSERT INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES($id,$attribute,$value)",
                    ("$id", skill.TypeId),
                    ("$attribute", attribute!["attributeID"]!.GetValue<int>()),
                    ("$value", double.Parse(attribute["value"]!.ToJsonString(), CultureInfo.InvariantCulture))
                );
            }
            foreach (var limit in skill.AlphaLimits)
            {
                string race = limit.RaceId.ToString(CultureInfo.InvariantCulture);
                JsonObject limits = alpha[race] as JsonObject ?? new();
                if (alpha[race] is null)
                {
                    alpha[race] = limits;
                }
                limits[key] = limit.Level;
                string raceKey = JsonSerializer.Serialize(race);
                var grade = Read("client/clonegrades", raceKey) ?? new();
                var rows = grade["skills"] as JsonArray ?? new();
                foreach (var row in rows.Where(row => row?["typeID"]?.GetValue<int>() == skill.TypeId).ToArray())
                {
                    rows.Remove(row);
                }
                rows.Add(new JsonObject { ["typeID"] = skill.TypeId, ["level"] = limit.Level });
                grade["skills"] = rows.DeepClone();
                Write("client/clonegrades", raceKey, grade);
            }
        }
        // The reverse index is grouped by required level, market group, and meta group.
        var changedIds = changes.Select(entry => entry.Definition.TypeId).ToHashSet();
        using (var command = target.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/requiredskillsindex'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var value = JsonNode.Parse(reader.GetString(1))!.AsObject();
                bool changed = RemoveReferences(value, changedIds);
                if (changed)
                {
                    indices[reader.GetString(0)] = value;
                }
            }
        }
        foreach (var entry in changes.Where(entry => !entry.Deleted))
        {
            foreach (var prerequisite in entry.Definition.Prerequisites)
            {
                string key = prerequisite.TypeId.ToString(CultureInfo.InvariantCulture);
                var index = indices[key] as JsonObject ?? Read("client/requiredskillsindex", key) ?? new();
                if (indices[key] is null)
                {
                    indices[key] = index;
                }
                string level = prerequisite.Level.ToString(CultureInfo.InvariantCulture);
                var groups = index[level] as JsonObject;
                if (groups is null)
                {
                    groups = new();
                    index[level] = groups;
                }
                string group = (entry.Definition.MarketGroupId ?? 0).ToString(CultureInfo.InvariantCulture);
                var metas = groups[group] as JsonObject;
                if (metas is null)
                {
                    metas = new();
                    groups[group] = metas;
                }
                var ids = metas["1"] as JsonArray;
                if (ids is null)
                {
                    ids = new();
                    metas["1"] = ids;
                }
                ids.Add(entry.Definition.TypeId);
            }
        }
        foreach (var pair in indices)
        {
            Write("client/requiredskillsindex", pair.Key, pair.Value!);
        }
        transaction.Commit();
        return new JsonObject
        {
            ["datasets"] = new JsonObject
            {
                ["types.fsdbinary"] = types,
                ["typedogma.fsdbinary"] = dogma,
                ["requiredskillsfortypes.fsdbinary"] = required,
                ["requiredskillsindex.fsdbinary"] = indices,
            },
            ["messages"] = messages,
            ["alpha"] = alpha,
            ["templates"] = templates,
        }.ToJsonString();

        JsonObject? Read(string dataset, string key)
        {
            using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Data FROM SourceRecord WHERE Dataset=$dataset AND Key=$key";
            command.Parameters.AddWithValue("$dataset", dataset);
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() is string text ? JsonNode.Parse(text)?.AsObject() : null;
        }

        void Write(string dataset, string key, JsonNode value) =>
            Execute(
                "INSERT INTO SourceRecord(Dataset,Key,Data) VALUES($dataset,$key,$data) "
                    + "ON CONFLICT(Dataset,Key) DO UPDATE SET Data=excluded.Data",
                ("$dataset", dataset),
                ("$key", key),
                ("$data", value.ToJsonString())
            );

        void Execute(string sql, params (string Name, object Value)[] values)
        {
            using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in values)
            {
                command.Parameters.AddWithValue(name, value);
            }
            command.ExecuteNonQuery();
        }
    }

    private static bool RemoveReferences(JsonNode node, IReadOnlySet<int> ids)
    {
        bool changed = false;
        if (node is JsonArray array)
        {
            foreach (
                var value in array
                    .Where(value => value is JsonValue number && number.TryGetValue<int>(out int id) && ids.Contains(id))
                    .ToArray()
            )
            {
                array.Remove(value);
                changed = true;
            }
        }
        else if (node is JsonObject map)
        {
            foreach (var pair in map)
            {
                if (pair.Value is not null)
                {
                    changed |= RemoveReferences(pair.Value, ids);
                }
            }
        }
        return changed;
    }
}
