"""Native readers for supported cFSD datasets from EVE build 3662168."""

from dataclasses import dataclass
from pathlib import Path
import struct
from typing import Callable, Iterator

_HEADER = struct.Struct("<24sQ")
_ROOT_MAP = struct.Struct("<QQQ")
_INT32 = struct.Struct("<i")
_UINT8 = struct.Struct("<B")
_UINT32 = struct.Struct("<I")
_UINT64 = struct.Struct("<Q")
_FLOAT64 = struct.Struct("<d")
_DOGMA_ATTRIBUTE = struct.Struct("<di4x")
_DOGMA_EFFECT = struct.Struct("<iB3x")

_Record = dict[str, int | float | list[int] | list[dict[str, int | float]]]


class _Container:
    def __init__(self, path: Path):
        self._data = path.read_bytes()
        self._path = path

    def require_range(self, offset: int, size: int) -> None:
        if offset < 0 or size < 0 or offset + size > len(self._data):
            raise ValueError(f"cFSD range {offset} + {size} is outside {self._path}.")

    def unpack(self, codec: struct.Struct, offset: int) -> tuple:
        self.require_range(offset, codec.size)
        return codec.unpack_from(self._data, offset)

    def number(self, codec: struct.Struct, offset: int) -> int | float:
        return self.unpack(codec, offset)[0]

    def list_entries(self, codec: struct.Struct, offset: int) -> Iterator[tuple]:
        first = _HEADER.size + self.number(_UINT64, offset)
        if first < _HEADER.size + _UINT64.size or first % _INT32.size:
            raise ValueError(f"Invalid cFSD list offset in {self._path}.")
        count = self.number(_UINT64, first - _UINT64.size)
        size = count * codec.size
        self.require_range(first, size)
        return codec.iter_unpack(memoryview(self._data)[first:first + size])

    def root_map(self, layout: "_Layout") -> Iterator[tuple[int, _Record]]:
        schema, payload_length = self.unpack(_HEADER, 0)
        if schema != layout.schema or payload_length != len(self._data) - _HEADER.size:
            raise ValueError(f"Unexpected build 3662168 cFSD header in {self._path}.")

        table_offset, row_count, bucket_count = self.unpack(_ROOT_MAP, _HEADER.size)
        if table_offset != _ROOT_MAP.size or row_count != layout.rows or not bucket_count:
            raise ValueError(f"Unexpected cFSD root map in {self._path}.")

        table = _HEADER.size + table_offset
        table_length = bucket_count * _UINT64.size
        self.require_range(table, table_length)

        keys = set()
        bucket_end = table + table_length
        for bucket in range(bucket_count):
            relative = self.number(_UINT64, table + bucket * _UINT64.size)
            if relative == 0:
                continue
            first = _HEADER.size + relative
            count_at = first - _UINT64.size
            if count_at < bucket_end or first % _UINT64.size:
                raise ValueError(f"Invalid cFSD bucket offset in {self._path}.")
            count = self.number(_UINT64, count_at)
            if count > row_count - len(keys):
                raise ValueError(f"Invalid cFSD bucket count in {self._path}.")
            bucket_length = count * layout.stride
            self.require_range(first, bucket_length)
            bucket_end = first + bucket_length

            for index in range(count):
                offset = first + index * layout.stride
                key = self.number(_INT32, offset)
                if key in keys or key % bucket_count != bucket:
                    raise ValueError(f"Invalid cFSD map key {key} in {self._path}.")
                keys.add(key)
                yield key, layout.decode(self, offset + layout.value_offset)

        if len(keys) != row_count:
            raise ValueError(f"Incomplete cFSD map in {self._path}.")


@dataclass(frozen=True)
class _Layout:
    schema: bytes
    rows: int
    stride: int
    value_offset: int
    decode: Callable[[_Container, int], _Record]


# The compiled schoolsLoader.pyd attribute getter identifies each field's
# offset, optional bit, and Python conversion. The two lists use relative
# element offsets, and the starter-school flag is exposed as an integer.
_SCHOOL_FIELDS = (
    ("careerID", 16, _INT32, 0),
    ("characterDescriptionID", 20, _INT32, 0x02),
    ("corporationID", 24, _INT32, 0),
    ("descriptionID", 28, _INT32, 0x04),
    ("iconID", 32, _INT32, 0x08),
    ("nameID", 36, _INT32, 0),
    ("raceID", 40, _INT32, 0),
    ("titleID", 44, _INT32, 0x40),
    ("is_starter_space_school", 48, _UINT8, 0x10),
)
_SCHOOL_LISTS = (
    ("careerAgents", 0, 0x01),
    ("startingStations", 8, 0x20),
)


def _school(container: _Container, offset: int) -> _Record:
    presence = container.number(_UINT32, offset + 52)
    if presence & ~0x7f:
        raise ValueError("Unknown cFSD school presence flags.")
    result: _Record = {
        name: container.number(codec, offset + field_offset)
        for name, field_offset, codec, mask in _SCHOOL_FIELDS
        if not mask or presence & mask
    }
    for name, field_offset, mask in _SCHOOL_LISTS:
        if presence & mask:
            result[name] = [value for value, in container.list_entries(_INT32, offset + field_offset)]
    return result


def _schoolmap(container: _Container, offset: int) -> _Record:
    return {
        "schoolID": container.number(_INT32, offset),
        "solarSystemID": container.number(_INT32, offset + 4),
    }


# Scalar widths, field offsets and presence bits match typesLoader.pyd and
# were checked against every source record in the build 3662168 reference.
_TYPE_FIELDS = (
    ("basePrice", 0, _FLOAT64, 0x01),
    ("capacity", 8, _FLOAT64, 0x02),
    ("certificateTemplate", 56, _INT32, 0x04),
    ("descriptionID", 60, _INT32, 0x08),
    ("factionID", 64, _INT32, 0x20),
    ("graphicID", 72, _INT32, 0x40),
    ("groupID", 76, _INT32, 0),
    ("iconID", 80, _INT32, 0x80),
    ("isDynamicType", 136, _UINT8, 0x100),
    ("isisGroupID", 84, _INT32, 0x200),
    ("marketGroupID", 88, _INT32, 0x400),
    ("mass", 24, _FLOAT64, 0x800),
    ("metaGroupID", 92, _INT32, 0x1000),
    ("metaLevel", 96, _INT32, 0x2000),
    ("portionSize", 32, _INT32, 0x4000),
    ("published", 137, _UINT8, 0x8000),
    ("quoteAuthorID", 100, _INT32, 0x10000),
    ("quoteID", 104, _INT32, 0x20000),
    ("raceID", 108, _INT32, 0x40000),
    ("radius", 40, _FLOAT64, 0x80000),
    ("soundID", 112, _INT32, 0x100000),
    ("techLevel", 116, _INT32, 0x200000),
    ("typeID", 120, _INT32, 0),
    ("typeNameID", 124, _INT32, 0),
    ("variationParentTypeID", 128, _INT32, 0x400000),
    ("volume", 48, _FLOAT64, 0x800000),
    ("wreckTypeID", 132, _INT32, 0x1000000),
)
_DESIGNER_LIST_MASK = 0x10


def _type_record(container: _Container, offset: int) -> _Record:
    presence = container.number(_UINT32, offset + 140)
    if presence & ~0x1ffffff:
        raise ValueError("Unknown cFSD type presence flags.")
    record: _Record = {
        name: container.number(codec, offset + field_offset)
        for name, field_offset, codec, mask in _TYPE_FIELDS
        if not mask or presence & mask
    }
    if presence & _DESIGNER_LIST_MASK:
        record["designerIDs"] = [value for value, in container.list_entries(_INT32, offset + 16)]
    return record


def _type_dogma(container: _Container, offset: int) -> _Record:
    # typeDogmaLoader.pyd exposes an ordered list of 16-byte attributes
    # and an ordered list of eight-byte effects with an integer default flag.
    return {
        "dogmaAttributes": [
            {"attributeID": attribute, "value": value}
            for value, attribute in container.list_entries(_DOGMA_ATTRIBUTE, offset)
        ],
        "dogmaEffects": [
            {"effectID": effect, "isDefault": default}
            for effect, default in container.list_entries(_DOGMA_EFFECT, offset + _UINT64.size)
        ],
    }


_LAYOUTS = {
    "schools": _Layout(bytes.fromhex("7bbbd5ec836a677f23667ea1b4c0a2e82eeaadfac74bac92"), 23, 64, 8, _school),
    "schoolmap": _Layout(bytes.fromhex("83ef52a27b97fc8bafdb9906dad1047736bc2eafc25d8f6c"), 12, 12, 4, _schoolmap),
    "types": _Layout(bytes.fromhex("4f25d0f64115864bd8c4f58da09c1758781f73cd2826500b"), 53299, 152, 8, _type_record),
    "typedogma": _Layout(bytes.fromhex("817d560b9962768e1a4b18598e9a1761e8832ca4d5733d00"), 27162, 24, 8, _type_dogma),
}


def supports(name: str) -> bool:
    """Return whether this pinned dataset has a native reader."""
    return name in _LAYOUTS


def records(path: Path, name: str) -> Iterator[tuple[int, _Record]]:
    """Read the pinned dataset as integer-keyed source records."""
    return _Container(path).root_map(_LAYOUTS[name])
