"""Reproduce native ship hold/volume test vectors using the pinned Windows client."""

import argparse
import ast
import ctypes
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import zipfile
import zlib


BUILD = 3662168
ARCHIVE_SHA256 = "37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368"

# Execute only verified constant modules and the inert cargo predicate. Stub the
# runtime services imported by inventoryFlagsCommon; no live client is started.
WORKER = r"""
import marshal, types, sys
job = eval(open(job_root + '/job.txt').read())
inv = types.ModuleType('inventorycommon')
inv.const = types.ModuleType('inventorycommon.const')
exec marshal.loads(open(job_root + '/inventory-const.pyc', 'rb').read()[8:]) in inv.const.__dict__
sys.modules['inventorycommon'] = inv
sys.modules['inventorycommon.const'] = inv.const
dogma = types.ModuleType('dogma')
dogma.const = types.ModuleType('dogma.const')
dogma.const.__dict__.update(job['attributeConstants'])
# Native constant spelling differs from the FSD attribute's name.
dogma.const.attributeSpecialOutputMoonMaterialBayCapacity = 5693
sys.modules['dogma'] = dogma
sys.modules['dogma.const'] = dogma.const
errors = types.ModuleType('eveexceptions')
errors.const = types.ModuleType('eveexceptions.const')
for name in ['UE_TYPEID', 'UE_CATID', 'UE_GROUPID']:
    setattr(errors.const, name, name)
sys.modules['eveexceptions'] = errors
sys.modules['eveexceptions.const'] = errors.const
sys.modules['evetypes'] = types.ModuleType('evetypes')
rules = {}
exec marshal.loads(open(job_root + '/hold-rules.pyc', 'rb').read()[8:]) in rules
holds = rules['inventoryFlagData']
flags = sorted(set(flag for hull in job['hulls'] for flag, rule in holds.items()
                   if hull['attributes'].get(rule['attribute'], 0) > 0))
ids = set(hull['id'] for hull in job['hulls'])
ids.update([34, 215, 1230, 25268, 16262, 2488, 23055, 33474, 2541, 16273, 16275])
first_group, first_category = {}, {}
for type_id, group, category in job['items']:
    first_group.setdefault(group, type_id)
    first_category.setdefault(category, type_id)
for flag in flags:
    rule = holds[flag]
    for key in ['allowGroups', 'blockGroups']:
        ids.update(first_group[group] for group in rule.get(key) or [] if group in first_group)
    for key in ['allowCategories']:
        ids.update(first_category[category] for category in rule.get(key) or [] if category in first_category)
    for key in ['allowTypes', 'blockTypes']:
        ids.update(rule.get(key) or [])
ids.add(first_category[inv.const.categorySubSystem])
ids.add(first_category[inv.const.categoryImplant])
items = [item for item in job['items'] if item[0] in ids]
accepted = {flag: [t for t, g, c in items if rules['ShouldAllowAdd'](flag, c, g, t) is None] for flag in flags}
result = (holds, flags, items, accepted, inv.const.packagedVolumeOverridesPerGroup,
          inv.const.packagedVolumeOverridesPerType)
open(job_root + '/native.txt', 'w').write(repr(result))
"""


def export(catalog_path, client_root, output):
    archive_path = client_root / "tq/code.ccp"
    if hashlib.sha256(archive_path.read_bytes()).hexdigest() != ARCHIVE_SHA256:
        raise ValueError("The script archive does not match stock build 3662168")
    with sqlite3.connect(catalog_path.resolve().as_uri() + "?mode=ro", uri=True) as catalog:
        def record(dataset, key):
            row = catalog.execute("SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?", (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else {}

        items = list(catalog.execute(
            "SELECT t.Id,t.GroupId,g.CategoryId FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId ORDER BY t.Id"
        ))
        hulls = []
        for type_id, group, category in items:
            if category != 6:
                continue
            source = record("client/types", type_id)
            if source.get("published") == 1 and source.get("techLevel") == 1:
                attributes = dict(catalog.execute(
                    "SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=?", (type_id,)
                ))
                attributes[38] = source.get("capacity", 0)
                hulls.append({"id": type_id, "group": group, "source": source, "attributes": attributes})
        if len(hulls) != 275:
            raise ValueError("Expected the pinned inventory of 275 Tech I hulls")
        constants = {}
        for key, data in catalog.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/dogmaattributes'"):
            name = json.loads(data).get("name")
            if name:
                constants["attribute" + name[0].upper() + name[1:]] = int(key)
        job = {"items": items, "hulls": [{"id": h["id"], "attributes": h["attributes"]} for h in hulls],
               "attributeConstants": constants}
        with tempfile.TemporaryDirectory(prefix="eden-native-inventory-") as temporary:
            directory = Path(temporary)
            library = directory / "client-python.zip"
            with zipfile.ZipFile(archive_path) as archive, zipfile.ZipFile(library, "w") as target:
                for name in dict.fromkeys(archive.namelist()):
                    if name.endswith(".pyj"):
                        target.writestr(name[:-1] + "c", zlib.decompress(archive.read(name)))
                (directory / "inventory-const.pyc").write_bytes(zlib.decompress(archive.read("inventorycommon/const.pyj")))
                (directory / "hold-rules.pyc").write_bytes(zlib.decompress(
                    archive.read("eve/common/script/util/inventoryFlagsCommon.pyj")
                ))
            (directory / "job.txt").write_text(repr(job), encoding="ascii")
            (directory / "worker.py").write_text(WORKER, encoding="ascii")
            binaries = (client_root / "tq/bin64").resolve()
            previous_directory = Path.cwd()
            try:
                os.chdir(binaries)
                with os.add_dll_directory(str(binaries)):
                    runtime = ctypes.CDLL(str(binaries / "python27.dll"))
                    ctypes.c_int.in_dll(runtime, "Py_NoSiteFlag").value = 1
                    runtime.Py_InitializeEx(0)
                    setup = "import sys\nsys.path[:]=%r\njob_root=%r\nexecfile(%r)" % (
                        [str(library), str(binaries)], str(directory), str(directory / "worker.py")
                    )
                    if runtime.PyRun_SimpleString(setup.encode("ascii", "backslashreplace")):
                        raise RuntimeError("Native inventory evaluation failed")
            finally:
                os.chdir(previous_directory)
            holds, flags, probes, accepted, groups, overrides = ast.literal_eval((directory / "native.txt").read_text())
        volumes = []
        for hull in hulls:
            volume = hull["source"]["volume"]
            volumes.append({"typeId": hull["id"], "groupId": hull["group"], "assembledVolume": volume,
                            "packagedVolume": overrides.get(hull["id"], groups.get(hull["group"], volume)),
                            "holds": [{"flag": flag, "attribute": holds[flag]["attribute"],
                                       "capacity": hull["attributes"][holds[flag]["attribute"]]}
                                      for flag in sorted(holds)
                                      if hull["attributes"].get(holds[flag]["attribute"], 0) > 0]})
        other_volumes = []
        for group in sorted(groups):
            first = next((item for item in items if item[1] == group), None)
            if first:
                source = record("client/types", first[0])
                other_volumes.append({"typeId": first[0], "groupId": group, "assembledVolume": source["volume"],
                                      "packagedVolume": overrides.get(first[0], groups[group]), "holds": []})
        data = {"build": BUILD, "source": "inventorycommon.const and inventoryFlagsCommon.ShouldAllowAdd from stock code.ccp",
                "archiveSha256": ARCHIVE_SHA256,
                "items": [{"typeId": t, "groupId": g, "categoryId": c} for t, g, c in probes],
                "holds": [{"flag": flag, "attribute": holds[flag]["attribute"], "allowedTypes": accepted[flag]} for flag in flags],
                "volumes": volumes, "otherShipVolumes": other_volumes}
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"Exported {len(volumes)} hulls, {len(flags)} holds and {len(probes)} native cargo probes.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, type=Path)
    parser.add_argument("--client", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    options = parser.parse_args()
    export(options.catalog.resolve(), options.client.resolve(), options.output.resolve())
