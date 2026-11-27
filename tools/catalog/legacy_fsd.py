"""Read the schema-driven 32-bit FSD resources in pinned build 3662168.

The binary layout follows CCP Games' MIT-licensed Carbon FSD reference at
https://github.com/carbonengine/fsd/tree/7fd6a10b1dd01685b0dd57f136fdff5eb9458241.
This reader expects input authenticated by the pinned client resource index.
"""

from collections import OrderedDict
from io import BytesIO
from pathlib import Path
import pickle
import re
from struct import Struct


U32 = Struct("<I")
U64 = Struct("<Q")
ENUM = {1: Struct("<B"), 2: Struct("<H"), 4: U32}
INT = Struct("<i")
FLOAT = {False: Struct("<f"), True: Struct("<d")}
VECTOR_WIDTH = {"vector2": 2, "vector3": 3, "vector4": 4, "color": 4}
VECTOR = {(length, double): Struct("<" + str(length) + ("d" if double else "f"))
          for length in set(VECTOR_WIDTH.values()) for double in FLOAT}
INTEGER_TYPES = {"int", "factionID", "fsdReference", "localizationID", "typeID"}


class _SchemaUnpickler(pickle.Unpickler):
    def find_class(self, module, name):
        if (module, name) == ("collections", "OrderedDict"):
            return OrderedDict
        raise pickle.UnpicklingError(f"Unsupported FSD schema type {module}.{name}")

    def persistent_load(self, identifier):
        raise pickle.UnpicklingError("FSD schema cannot reference external objects")


def _yaml_scalar(token):
    if token in ("true", "false", "null", "''"):
        return {"true": True, "false": False, "null": None, "''": ""}[token]
    if re.fullmatch(r"-?[0-9]+", token):
        return int(token)
    if re.fullmatch(r"[\w.-]+", token):
        return token
    raise ValueError(f"Unsupported external FSD schema scalar {token!r}")


def _yaml_pair(text):
    key, delimiter, value = text.partition(":")
    if not delimiter:
        raise ValueError(f"Invalid external FSD schema field {text!r}")
    return _yaml_scalar(key.strip()), value.strip()


def _yaml_value(text, anchors):
    if text.startswith("{") and text.endswith("}"):
        members = (_yaml_pair(part) for part in text[1:-1].split(",") if part.strip())
        return {key: _yaml_scalar(value) for key, value in members}
    if text.startswith("[") and text.endswith("]"):
        return [_yaml_scalar(item.strip()) for item in text[1:-1].split(",") if item.strip()]
    if text.startswith("*"):
        return anchors[text[1:]]
    return _yaml_scalar(text)


def _schema_fields(text):
    # The external schemas have block mappings and scalar flow maps/lists, no tags.
    pending = ""
    level = 0
    for line in text.splitlines():
        if not line.strip():
            continue
        if pending:
            pending += " " + line.strip()
        else:
            level = len(line) - len(line.lstrip(" "))
            pending = line.strip()
        if pending.count("{") == pending.count("}") and pending.count("[") == pending.count("]"):
            yield level, pending
            pending = ""
    if pending:
        raise ValueError("Unclosed collection in external FSD schema")


def _yaml_schema(text):
    root = {}
    ancestors = [(0, root)]
    anchors = {}
    for level, field in _schema_fields(text):
        while level < ancestors[-1][0]:
            ancestors.pop()
        expected_level, current = ancestors[-1]
        key, value = _yaml_pair(field)
        if level != expected_level or key in current:
            raise ValueError(f"Invalid external FSD schema mapping at {field!r}")
        if value and not value.startswith("&"):
            current[key] = _yaml_value(value, anchors)
            continue
        child = {}
        current[key] = child
        ancestors.append((level + 2, child))
        if value:
            anchors[value[1:]] = child
    return root


class _Reader:
    def __init__(self, data):
        self.data = memoryview(data)

    def _range(self, offset, length):
        if offset < 0 or length < 0 or offset + length > len(self.data):
            raise ValueError(f"FSD range {offset}+{length} exceeds {len(self.data)} bytes")

    def _slice(self, offset, length):
        self._range(offset, length)
        return self.data[offset:offset + length]

    def _object(self, offset, schema):
        fixed = schema["constantAttributeOffsets"]
        variable = {}
        if "size" not in schema:
            mask_offset = offset + schema["endOfFixedSizeData"]
            mask, = U64.unpack_from(self.data, mask_offset)
            optional = schema["optionalValueLookups"]
            names = [name for name in schema["attributesWithVariableOffsets"]
                     if name not in optional or mask & optional[name]]
            offsets = mask_offset + U64.size
            values = offsets + U32.size * len(names)
            variable = {name: values + position
                        for name, (position,) in zip(names, U32.iter_unpack(self._slice(offsets, values - offsets)))}
        result = {}
        for name, child in schema["attributes"].items():
            if name in fixed:
                result[name] = self.value(offset + fixed[name], child)
            elif name in variable:
                result[name] = self.value(variable[name], child)
            elif "default" in child:
                result[name] = child["default"]
            elif not child.get("isOptional"):
                raise ValueError(f"Missing required FSD field {name!r} at {offset}")
        return result

    def items(self, offset, schema):
        item = schema["itemTypes"]
        start = offset
        if "length" in schema:
            count = schema["length"]
        else:
            count, = U32.unpack_from(self.data, offset)
            start += U32.size
        width = schema.get("fixedItemSize", item.get("size"))
        if width is not None:
            self._range(start, count * width)
            for position in range(start, start + count * width, width):
                yield self.value(position, item)
            return
        offsets = self._slice(start, count * U32.size)
        for relative, in U32.iter_unpack(offsets):
            yield self.value(offset + relative, item)

    def entries(self, offset, schema):
        footer_end = offset + U32.unpack_from(self.data, offset)[0]
        footer_size, = U32.unpack_from(self.data, footer_end)
        footer_start = footer_end - footer_size
        body = offset + U32.size
        if footer_start < body:
            raise ValueError(f"FSD dictionary at {offset} has an invalid footer")
        footer = _Reader(self._slice(footer_start, footer_size))
        value_schema = schema["valueTypes"]
        for entry in footer.items(0, schema["keyFooter"]):
            position = body + entry["offset"]
            if not body <= position < footer_start:
                raise ValueError(f"FSD dictionary at {offset} has an invalid record offset")
            yield entry["key"], self.value(position, value_schema)

    def value(self, offset, schema):
        kind = schema["type"]
        if kind == "object":
            return self._object(offset, schema)
        if kind == "list":
            return list(self.items(offset, schema))
        if kind == "dict":
            return dict(self.entries(offset, schema))
        if kind in INTEGER_TYPES:
            decoder = U32 if schema.get("min", -1) >= 0 or schema.get("exclusiveMin", -2) >= -1 else INT
            return decoder.unpack_from(self.data, offset)[0]
        if kind == "float":
            return FLOAT[schema.get("precision") == "double"].unpack_from(self.data, offset)[0]
        if kind == "bool":
            return self.data[offset] == 255
        if kind in ("string", "resPath", "unicode"):
            size, = U32.unpack_from(self.data, offset)
            encoding = "utf-8" if kind == "unicode" else "cp1252"
            return self._slice(offset + U32.size, size).tobytes().decode(encoding)
        if kind == "enum":
            value, = ENUM[schema["size"]].unpack_from(self.data, offset)
            if schema.get("readEnumValue"):
                return value
            return next((name for name, number in schema["values"].items() if number == value), None)
        if kind in VECTOR_WIDTH:
            values = VECTOR[VECTOR_WIDTH[kind], schema.get("precision") == "double"].unpack_from(self.data, offset)
            return {name: values[index] for name, index in schema["aliases"].items()} if "aliases" in schema else values
        raise ValueError(f"Unsupported FSD field type {kind!r}")


def read_records(source: Path, schema_path: Path | None = None):
    """Yield the same root keys and values as the pinned client loader."""
    data = source.read_bytes()
    if schema_path is None:
        schema_size, = U32.unpack_from(data)
        start = U32.size + schema_size
        if not U32.size < start <= len(data):
            raise ValueError(f"Invalid embedded FSD schema size in {source}")
        schema = _SchemaUnpickler(BytesIO(data[U32.size:start]), encoding="utf-8").load()
    else:
        start = 0
        schema = _yaml_schema(schema_path.read_text(encoding="utf-8"))
    reader = _Reader(memoryview(data)[start:])
    if schema["type"] == "dict":
        yield from reader.entries(0, schema)
    elif schema["type"] == "list":
        yield from enumerate(reader.items(0, schema))
    else:
        yield "root", reader.value(0, schema)
