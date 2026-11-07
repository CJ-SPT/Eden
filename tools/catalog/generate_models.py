"""Regenerate the build-pinned C# source types from the decoded catalog."""
import argparse
import collections
from contextlib import closing
import copy
import json
from pathlib import Path
import re
import sqlite3


def name(value):
    words = re.findall(r"[A-Za-z0-9]+", value)
    result = "".join(w[:1].upper() + w[1:] for w in words)
    return result if result and result[0].isalpha() else "Field" + result


class Shape:
    """Union observed fields and optional values without retaining source rows."""
    def __init__(self):
        self.kind = None
        self.fields = {}
        self.child = None
        self.alternatives = {}
        self.allows_null = False
        self.wide_integer = False

    def observe(self, value):
        if value is None:
            self.allows_null = True
            return
        kind = {int: "long", float: "double", bool: "bool", str: "string",
                dict: "object", list: "array"}[type(value)]
        if self.kind is None:
            self.kind = kind
        if self.kind == "union":
            self.alternatives.setdefault(kind, Shape()).observe(value)
            return
        if self.kind == "double" and kind == "long" and abs(value) <= 2**53:
            return
        if self.kind == "long" and kind == "double" and not self.wide_integer:
            self.kind = kind
        if kind == "long":
            self.wide_integer |= abs(value) > 2**53
        if self.kind == "map" and kind == "object":
            for item in value.values():
                self.child.observe(item)
            return
        if self.kind != kind:
            previous = Shape()
            previous.__dict__ = self.__dict__.copy()
            self.kind = "union"
            self.fields = {}
            self.child = None
            self.alternatives = {previous.kind: previous, kind: Shape()}
            self.alternatives[kind].observe(value)
            return
        if kind == "array":
            if self.child is None:
                self.child = Shape()
            for item in value:
                self.child.observe(item)
        elif kind == "object":
            if len(self.fields.keys() | value.keys()) > 80 or any(
                    not re.fullmatch(r"[a-zA-Z_][a-zA-Z0-9_-]{0,70}", k) for k in value):
                self.kind = "map"
                self.child = Shape()
                for field in self.fields.values():
                    self.child.merge(field)
                self.fields = {}
                for item in value.values():
                    self.child.observe(item)
            else:
                for key, item in value.items():
                    if key not in self.fields:
                        self.fields[key] = Shape()
                    self.fields[key].observe(item)

    def merge(self, other):
        """Merge observations without discarding earlier map values."""
        self.allows_null |= other.allows_null
        if other.kind is None:
            return
        if self.kind is None:
            self.__dict__ = copy.deepcopy(other.__dict__)
            return
        if other.kind == "union":
            for alternative in other.alternatives.values():
                self.merge(alternative)
            return
        if self.kind == "union":
            self.alternatives.setdefault(other.kind, Shape()).merge(other)
            return
        if {self.kind, other.kind} <= {"long", "double"} and (
                self.kind == other.kind or not (self.wide_integer or other.wide_integer)):
            self.kind = "double" if "double" in (self.kind, other.kind) else "long"
            self.wide_integer |= other.wide_integer
            return
        if self.kind != other.kind and {self.kind, other.kind} != {"object", "map"}:
            previous = Shape()
            previous.__dict__ = copy.deepcopy(self.__dict__)
            self.kind = "union"
            self.fields = {}
            self.child = None
            self.alternatives = {previous.kind: previous, other.kind: Shape()}
            self.alternatives[other.kind].merge(other)
            return
        if self.kind in ("object", "map"):
            if self.kind == "map" or other.kind == "map":
                if self.kind != "map":
                    self.kind = "map"
                    self.child = Shape()
                    for field in self.fields.values():
                        self.child.merge(field)
                    self.fields = {}
                if other.kind == "map":
                    self.child.merge(other.child)
                else:
                    for field in other.fields.values():
                        self.child.merge(field)
            else:
                for key, field in other.fields.items():
                    self.fields.setdefault(key, Shape()).merge(field)
        elif self.kind == "array":
            if self.child is None:
                self.child = Shape()
            if other.child is not None:
                self.child.merge(other.child)

    def schema(self):
        result = {"kind": self.kind or "absent", "allowsNull": self.allows_null,
                  "wideInteger": self.wide_integer}
        if self.fields:
            result["fields"] = {k: v.schema() for k, v in sorted(self.fields.items())}
        if self.child is not None:
            result["element"] = self.child.schema()
        if self.alternatives:
            result["alternatives"] = {k: v.schema() for k, v in sorted(self.alternatives.items())}
        return result

    @staticmethod
    def from_schema(schema):
        shape = Shape()
        shape.kind = None if schema["kind"] == "absent" else schema["kind"]
        shape.allows_null = schema["allowsNull"]
        shape.wide_integer = schema.get("wideInteger", False)
        shape.fields = {k: Shape.from_schema(v) for k, v in schema.get("fields", {}).items()}
        shape.child = Shape.from_schema(schema["element"]) if "element" in schema else None
        shape.alternatives = {k: Shape.from_schema(v) for k, v in schema.get("alternatives", {}).items()}
        return shape


class Emitter:
    def __init__(self):
        self.definitions = []
        self.known = {}
        self.used = set()

    def type(self, shape, hint):
        if not shape or shape.kind is None:
            return "CatalogAbsent"
        if shape.kind == "union":
            return self.union(shape, hint)
        if shape.kind == "array":
            return "ImmutableArray<" + self.type(shape.child, hint + "Item") + ">"
        if shape.kind == "map" or (shape.kind == "object" and not shape.fields):
            return "ImmutableDictionary<string, " + self.type(shape.child, hint + "Value") + ">"
        if shape.kind != "object":
            return shape.kind
        fields = tuple((key, self.type(field, hint + name(key))) for key, field in sorted(shape.fields.items()))
        if fields in self.known:
            return self.known[fields]
        typename = hint
        while typename in self.used:
            typename += "Data"
        self.used.add(typename)
        self.known[fields] = typename
        members = []
        names = {typename}
        for key, kind in fields:
            prop = name(key)
            while prop in names:
                prop += "Value"
            names.add(prop)
            members.append(f'    [JsonPropertyName({json.dumps(key)})] public {kind}? {prop} {{ get; init; }}')
        self.definitions.append('[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]\n' +
                                f"public sealed class {typename}\n{{\n" + "\n".join(members) + "\n}")
        return typename

    def union(self, shape, hint):
        """Emit a named, token-discriminated union with typed alternatives."""
        typename = hint + "Choice"
        while typename in self.used:
            typename += "Data"
        self.used.add(typename)
        variants = []
        tokens = {"long": "Number", "double": "Number", "string": "String", "bool": "True or JsonTokenType.False",
                  "array": "StartArray", "object": "StartObject", "map": "StartObject"}
        for kind, alternative in sorted(shape.alternatives.items()):
            variants.append((kind, self.type(alternative, hint + name(kind)), tokens[kind]))
        # Prefer an exact Int64 branch before floating-point when both are needed.
        numbers = [v for v in variants if v[0] in ("long", "double")]
        if len(numbers) == 2:
            variants.sort(key=lambda v: v[0] != "long")
        if any(v[0] == "map" for v in variants) and any(v[0] == "object" for v in variants):
            merged = Shape()
            merged.merge(shape.alternatives["map"])
            merged.merge(shape.alternatives["object"])
            variants = [v for v in variants if v[0] not in ("map", "object")]
            variants.append(("map", self.type(merged, hint + "Map"), "StartObject"))
        declarations = [f'[JsonConverter(typeof({typename}Converter))]\npublic abstract record {typename};']
        for kind, value_type, _ in variants:
            declarations.append(f'public sealed record {typename}{name(kind)}({value_type} Value) : {typename};')
        reads = []
        for kind, value_type, token in variants:
            guard = " when reader.TryGetInt64(out _)" if kind == "long" and len(numbers) == 2 else ""
            reads.append(f'            JsonTokenType.{token}{guard} => new {typename}{name(kind)}('
                         f'JsonSerializer.Deserialize<{value_type}>(ref reader, options)!),')
        writes = [f'            case {typename}{name(kind)} item:\n                JsonSerializer.Serialize(writer, item.Value, options);\n                break;'
                  for kind, _, _ in variants]
        declarations.append(f'''internal sealed class {typename}Converter : JsonConverter<{typename}>
{{
    public override {typename} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {{
{chr(10).join(reads)}
            _ => throw new JsonException("Unexpected {typename} token."),
        }};

    public override void Write(Utf8JsonWriter writer, {typename} value, JsonSerializerOptions options)
    {{
        switch (value)
        {{
{chr(10).join(writes)}
            default:
                throw new JsonException("Unexpected {typename} variant.");
        }}
    }}
}}''')
        self.definitions.extend(declarations)
        return typename


def generate(database, output, models_output=None, schema_output=None, schema_cache=None):
    emitter = Emitter()
    sources = collections.defaultdict(list)
    descriptors = []
    relationships = json.loads(Path(__file__).with_name("relationships-3662168.json").read_text(encoding="utf-8"))
    with closing(sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)) as db:
        inventory = list(db.execute("SELECT Name,Source,Sha256,Rows FROM SourceDataset ORDER BY Name"))
        cached = json.loads(schema_cache.read_text(encoding="utf-8")) if schema_cache and schema_cache.exists() else None
        if cached is not None and cached["inventory"] != [list(row) for row in inventory]:
            raise ValueError("Inferred schema cache does not match the installed catalog identity.")
        shapes = {}
        for dataset, source_uri, sha, rows in inventory:
            if cached is None:
                shape, key_shape = Shape(), Shape()
                for raw_key, raw in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset=? ORDER BY Key", (dataset,)):
                    key_shape.observe(json.loads(raw_key))
                    shape.observe(json.loads(raw))
            else:
                shape = Shape.from_schema(cached["shapes"][dataset]["value"])
                key_shape = Shape.from_schema(cached["shapes"][dataset]["key"])
            shapes[dataset] = {"key": key_shape.schema(), "value": shape.schema()}
            family, source = dataset.split("/", 1)
            kind = emitter.type(shape, name(family) + name(source))
            key_type = emitter.type(key_shape, name(family) + name(source) + "Key")
            member = name(source) if name(source) != name(family) else "Metadata"
            sources[name(family)].append(f'        public static readonly CatalogSource<{key_type}, {kind}> {member} = new("{dataset}");')
            descriptors.append({"name": dataset, "source": source_uri, "sha256": sha, "records": rows,
                                "keyType": key_type, "valueType": kind, "keySchema": key_shape.schema(),
                                "valueSchema": shape.schema(), "relationships": relationships.get(dataset, [])})
        if schema_cache is not None and cached is None:
            schema_cache.parent.mkdir(parents=True, exist_ok=True)
            schema_cache.write_text(json.dumps({"inventory": inventory, "shapes": shapes}, ensure_ascii=False), encoding="utf-8")
    header = """// <auto-generated by tools/catalog/generate_models.py for build 3662168>
#nullable enable
using Eden.Data.Catalog;
using System.Collections.Immutable;
using System.Text.Json;

namespace Eden.World.Catalog;

"""
    groups = [f"    public static class {family}\n    {{\n" + "\n".join(rows) + "\n    }"
              for family, rows in sources.items()]
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(header + "public static class CatalogData\n{\n" + "\n\n".join(groups) + "\n}\n", encoding="utf-8")
    if models_output is None:
        models_output = output.parents[2] / "Eden.Data/Catalog/CatalogModels.g.cs"
    model_header = header.replace("using Eden.Data.Catalog;\n", "").replace(
        "namespace Eden.World.Catalog;", "namespace Eden.Data.Catalog;").replace(
        "using System.Text.Json;", "using System.Text.Json;\nusing System.Text.Json.Serialization;")
    models_output.parent.mkdir(parents=True, exist_ok=True)
    models_output.write_text(model_header + "\n\n".join(emitter.definitions) + "\n", encoding="utf-8")
    if schema_output is None:
        schema_output = models_output.with_name("CatalogSchema.g.json")
    if schema_output is not None:
        known_datasets = {descriptor["name"] for descriptor in descriptors}
        for descriptor in descriptors:
            for relationship in descriptor["relationships"]:
                if relationship["target"] not in known_datasets or not schema_path_exists(
                        descriptor["valueSchema"], relationship["path"].split("/")[1:]):
                    raise ValueError(f"Invalid catalog relationship: {descriptor['name']} {relationship}")
        schema_output.parent.mkdir(parents=True, exist_ok=True)
        report = {"build": 3662168, "datasetCount": len(descriptors), "recordCount": sum(d["records"] for d in descriptors),
                  "unresolvedFields": 0, "datasets": descriptors}
        schema_output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def schema_path_exists(schema, parts):
    if not parts:
        return True
    if schema["kind"] == "union":
        return any(schema_path_exists(variant, parts) for variant in schema["alternatives"].values())
    if parts[0] == "*":
        return "element" in schema and schema_path_exists(schema["element"], parts[1:])
    field = schema.get("fields", {}).get(parts[0])
    return field is not None and schema_path_exists(field, parts[1:])


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("database", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--models-output", type=Path)
    parser.add_argument("--schema-output", type=Path)
    parser.add_argument("--schema-cache", type=Path)
    args = parser.parse_args()
    generate(args.database, args.output, args.models_output, args.schema_output, args.schema_cache)
