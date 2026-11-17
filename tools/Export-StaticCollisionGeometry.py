"""Bake conservative static hull collision primitives from pinned local client meshes.

Knit is an external offline converter, not an Eden dependency. Its output is input data;
no converter source is incorporated. The catalog is opened read-only, as in the other
catalog fixture exporters. Generated primitives are shared by simulation and Destiny.
"""
import argparse
import collections
import concurrent.futures
import gzip
import hashlib
import itertools
import json
import math
import pathlib
import re
import shutil
import sqlite3
import struct
import subprocess
import time


GENERATOR = 'mesh-surface-capsules-v2'
RESOURCE_INDEX_SHA256 = 'cba0852b46e3a1501c6c0653546c4520dbd6aba02a51e1da3409f5efeb253f03'
# Build 3662168 inventory categories and fixed collidable scenery groups.
STRUCTURE_CATEGORIES = (3, 22, 23, 40, 46, 65)
STATIC_GROUPS = (10, 226, 319, 784, 4073)


def accessor(document, root, index):
    entry = document['accessors'][index]
    view = document['bufferViews'][entry['bufferView']]
    data = (root / document['buffers'][view['buffer']]['uri']).read_bytes()
    width = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}[entry['type']]
    kind = {5121: 'B', 5123: 'H', 5125: 'I', 5126: 'f'}[entry['componentType']]
    fmt = '<' + kind * width
    step = view.get('byteStride', struct.calcsize(fmt))
    start = view.get('byteOffset', 0) + entry.get('byteOffset', 0)
    return [struct.unpack_from(fmt, data, start + i * step) for i in range(entry['count'])]


IDENTITY = (1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)


def multiply(a, b):
    return tuple(sum(a[k * 4 + row] * b[col * 4 + k] for k in range(4))
                 for col in range(4) for row in range(4))


def point(matrix, value):
    return tuple(sum(matrix[col * 4 + row] * value[col] for col in range(3)) + matrix[12 + row]
                 for row in range(3))


def transform(node):
    if 'matrix' in node:
        return tuple(node['matrix'])
    x, y, z, w = node.get('rotation', (0, 0, 0, 1))
    scale = node.get('scale', (1, 1, 1))
    translation = node.get('translation', (0, 0, 0))
    matrix = [1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w), 0,
              2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w), 0,
              2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y), 0,
              *translation, 1]
    for column in range(3):
        for row in range(3):
            matrix[column * 4 + row] *= scale[column]
    return tuple(matrix)


def triangles(path):
    document = json.loads(path.read_text())
    nodes = document['nodes']
    parents = {child: index for index, node in enumerate(nodes) for child in node.get('children', [])}
    transforms = {}
    values = {}

    def world(index):
        if index not in transforms:
            local = transform(nodes[index])
            transforms[index] = multiply(world(parents[index]), local) if index in parents else local
        return transforms[index]

    def read(index):
        if index not in values:
            values[index] = accessor(document, path.parent, index)
        return values[index]

    # A Granny file can contain every render LOD. Keep each named part's highest-detail mesh.
    selected = {}
    for index, mesh in enumerate(document['meshes']):
        name = mesh.get('name', str(index))
        match = re.search(r'\s+LOD\s*(\d+)', name, re.I)
        key = name[:match.start()] if match else name
        detail = int(match.group(1)) if match else math.inf
        if key not in selected or detail > selected[key][0]:
            selected[key] = (detail, index)
    selected_ids = {value[1] for value in selected.values()}
    result = []
    for node_index, node in enumerate(nodes):
        if node.get('mesh') not in selected_ids:
            continue
        mesh = document['meshes'][node['mesh']]
        for primitive in mesh['primitives']:
            if primitive.get('mode', 4) != 4:
                raise ValueError('Only triangle meshes are supported')
            attributes = primitive['attributes']
            vertices = read(attributes['POSITION'])
            if 'skin' in node and 'JOINTS_0' in attributes and 'WEIGHTS_0' in attributes:
                skin = document['skins'][node['skin']]
                inverse = read(skin['inverseBindMatrices'])
                joint_matrices = [multiply(world(joint), inverse[i]) for i, joint in enumerate(skin['joints'])]
                joints = read(attributes['JOINTS_0'])
                weights = read(attributes['WEIGHTS_0'])
                posed = []
                for vertex, joint, weight in zip(vertices, joints, weights):
                    positions = [(point(joint_matrices[j], vertex), w) for j, w in zip(joint, weight) if w > 0]
                    posed.append(tuple(sum(p[i] * w for p, w in positions) for i in range(3)))
                vertices = posed
            else:
                vertices = [point(world(node_index), vertex) for vertex in vertices]
            indices = [x[0] for x in read(primitive['indices'])]
            result.extend(tuple(vertices[x] for x in indices[i:i + 3]) for i in range(0, len(indices), 3))
    if not result:
        raise ValueError(f'No mesh triangles: {path}')
    return result


def sample(mesh, pitch):
    cells = set()
    stack = mesh.copy()
    squared_limit = pitch * pitch / 4
    while stack:
        a, b, c = stack.pop()
        pairs = ((a, b, c), (b, c, a), (c, a, b))
        lengths = [sum((v[i] - w[i]) ** 2 for i in range(3)) for v, w, _ in pairs]
        edge = max(range(3), key=lengths.__getitem__)
        if lengths[edge] > squared_limit:
            v, w, other = pairs[edge]
            midpoint = tuple((v[i] + w[i]) / 2 for i in range(3))
            stack.append((v, midpoint, other))
            stack.append((midpoint, w, other))
        else:
            centre = tuple((a[i] + b[i] + c[i]) / 3 for i in range(3))
            cells.add(tuple(math.floor(centre[i] / pitch + 0.5) for i in range(3)))
    # A leaf triangle lies within pitch/3 of its centroid. Quantization adds
    # at most sqrt(3)*pitch/2, so 1.2*pitch conservatively covers its surface.
    return sorted(cells)


def merge(cells):
    remaining = set(cells)
    runs = []
    for axis in range(3):
        lines = {}
        other = [index for index in range(3) if index != axis]
        for cell in cells:
            lines.setdefault((cell[other[0]], cell[other[1]]), []).append(cell[axis])
        for key, entries in sorted(lines.items()):
            entries.sort()
            start = previous = entries[0]
            for value in entries[1:] + [entries[-1] + 2]:
                if value != previous + 1:
                    if previous - start >= 2:
                        a = [0, 0, 0]
                        a[axis], a[other[0]], a[other[1]] = start, *key
                        runs.append((previous - start + 1, axis, tuple(a)))
                    start = value
                previous = value
    capsules = []
    for length, axis, a in sorted(runs, key=lambda run: (-run[0], run[1], run[2])):
        start = None
        for offset in range(length + 1):
            cell = list(a)
            cell[axis] += offset
            cell = tuple(cell)
            if offset < length and cell in remaining:
                if start is None:
                    start = offset
            elif start is not None:
                if offset - start >= 3:
                    first, last = list(a), list(a)
                    first[axis] += start
                    last[axis] += offset - 1
                    capsules.append((tuple(first), tuple(last)))
                    for remove in range(start, offset):
                        value = list(a)
                        value[axis] += remove
                        remaining.remove(tuple(value))
                start = None
    return sorted(remaining), sorted(capsules)


def vector(value):
    return dict(zip(('x', 'y', 'z'), value))


def bake(source_name, refresh=False):
    source = pathlib.Path(source_name)
    cache_path = source.with_suffix('.baked.json')
    checksum = source.with_suffix('.md5').read_text()
    if cache_path.exists() and not refresh:
        cached = json.loads(cache_path.read_text())
        if cached.get('generator') == GENERATOR and cached.get('md5') == checksum:
            return cached['profile']
    mesh = triangles(source)
    values = [v for triangle in mesh for v in triangle]
    extent = max(max(v[i] for v in values) - min(v[i] for v in values) for i in range(3))
    # Keep the original large-hull quantization, scaling down for small deployables and props.
    quantum = 25 if extent >= 1200 else max(1 / 1024, extent / 1000)
    minimum = 100 if extent >= 1200 else quantum
    pitch = max(minimum, math.ceil(extent / 120 / quantum) * quantum)
    start = time.monotonic()
    while True:
        cells = sample(mesh, pitch)
        spheres, capsules = merge(cells)
        if len(spheres) + len(capsules) <= 2000:
            break
        pitch = math.ceil(pitch * 1.25 / quantum) * quantum
    output = {'key': source.stem, 'pitch': pitch, 'radius': pitch * 1.2,
              'spheres': [vector(cell) for cell in spheres],
              'capsules': [dict(start=vector(a), end=vector(b)) for a, b in capsules],
              'bounds': dict(min=vector(min(v[i] for v in values) for i in range(3)),
                             max=vector(max(v[i] for v in values) for i in range(3))),
              'triangles': len(mesh)}
    print(source.name, 'pitch', pitch, 'spheres', len(spheres), 'capsules', len(capsules),
          'seconds', round(time.monotonic() - start, 2), flush=True)
    cache_path.write_text(json.dumps(dict(generator=GENERATOR, md5=checksum, profile=output)), encoding='utf-8')
    return output


def prepare(args):
    index = {}
    source_index = (args.client / 'resfileindex.txt').read_bytes()
    if hashlib.sha256(source_index).hexdigest() != RESOURCE_INDEX_SHA256:
        raise ValueError('The resource index does not match pinned client build 3662168')
    for line in source_index.decode('utf-8-sig').splitlines():
        fields = line.split(',')
        index[fields[0].lower()] = fields

    def resource(key):
        entry = index[key.lower()]
        path = args.resources / entry[1]
        if not path.is_file():
            raise FileNotFoundError(f'Client resource is not cached: {key}')
        if hashlib.md5(path.read_bytes()).hexdigest() != entry[2]:
            raise ValueError(f'Resource checksum differs: {key}')
        return path, entry[2]

    reusable = {}
    if args.output.exists() and not args.refresh:
        previous = json.loads(gzip.decompress(args.output.read_bytes()))
        if previous.get('build') == 3662168:
            for profile in previous.get('profiles', []):
                bounds = profile['bounds']
                extent = max(bounds['max'][axis] - bounds['min'][axis] for axis in ('x', 'y', 'z'))
                if (previous.get('generator') == GENERATOR
                        or previous.get('generator') == 'mesh-surface-capsules-v1' and extent >= 1200):
                    reusable[profile['md5']] = profile
    missing, types, models = [], [], {}
    with sqlite3.connect(args.catalog.resolve().as_uri() + '?mode=ro', uri=True) as catalog:
        build = json.loads(catalog.execute("SELECT Data FROM SourceRecord WHERE Dataset='sde/_sde' LIMIT 1").fetchone()[0])
        if build['buildNumber'] != 3662168:
            raise ValueError('This exporter requires pinned catalog build 3662168')

        def record(dataset, key):
            row = catalog.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else {}

        categories = ','.join(str(value) for value in STRUCTURE_CATEGORIES)
        groups = ','.join(str(value) for value in STATIC_GROUPS)
        predicate = f'g.CategoryId IN ({categories})'
        if args.scope == 'all-static':
            predicate += f' OR g.Id IN ({groups})'
        rows = catalog.execute('SELECT t.Id,t.Name FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId '
                               f'WHERE {predicate} ORDER BY t.Id').fetchall()
        for type_id, name in rows:
            if args.types and type_id not in args.types:
                continue
            try:
                graphic_id = record('client/types', type_id).get('graphicID')
                if not graphic_id:
                    raise ValueError('No graphic is assigned to this type')
                graphic = record('client/graphicids', graphic_id)
                hull = graphic.get('sofHullName')
                graphic_file = graphic.get('graphicFile')
                graphic_resource = f'res:/dx9/model/spaceobjectfactory/hulls/{hull}.black' if hull else graphic_file
                if not graphic_resource:
                    raise ValueError('No model is assigned to this type')
                if graphic_resource.lower().endswith('.red') and graphic_resource[:-4].lower() + '.black' in index:
                    graphic_resource = graphic_resource[:-4] + '.black'
                black, _ = resource(graphic_resource)
                paths = re.findall(rb'res:/[^\x00\r\n"<>]+?\.gr2', black.read_bytes())
                if not paths:
                    raise ValueError('The graphic has no static mesh (for example a procedural effect)')
                geometry = paths[0].decode()
                mesh, checksum = resource(geometry)
                key = 'mesh_' + checksum
                if key not in models:
                    destination = args.work / (key + '.gr2')
                    source_stamp = destination.with_suffix('.md5')
                    cached = dict(reusable[checksum], key=key) if checksum in reusable else None
                    if not args.preflight and cached is None and (not source_stamp.exists() or source_stamp.read_text() != checksum):
                        shutil.copyfile(mesh, destination)
                        source_stamp.write_text(checksum)
                    models[key] = dict(path=destination, geometry=geometry, md5=checksum, size=mesh.stat().st_size,
                                       sha256=hashlib.sha256(mesh.read_bytes()).hexdigest(), cached=cached)
                types.append(dict(typeId=type_id, graphicId=graphic_id, profile=key, hull=hull,
                                  graphicFile=graphic_file if not hull else None))
            except (KeyError, FileNotFoundError, IndexError) as error:
                missing.append(dict(typeId=type_id, name=name, reason=str(error)))
            except ValueError as error:
                if str(error).startswith('Resource checksum differs:'):
                    raise
                missing.append(dict(typeId=type_id, name=name, reason=str(error)))
    convert = [str(model['path'].resolve()) for model in models.values()
               if model['cached'] is None and not model['path'].with_suffix('.gltf').exists()]
    if convert and not args.preflight:
        with (args.work / 'conversion.log').open('w') as log:
            # Bounded batches stay below Windows' command-line length limit.
            for offset in range(0, len(convert), 16):
                subprocess.run(['dotnet', str(args.converter.resolve()), *convert[offset:offset + 16]],
                               check=True, stdout=log, stderr=subprocess.STDOUT)
                print(f'Converted {min(offset + 16, len(convert))}/{len(convert)} mesh inputs', flush=True)
        failed = [key for key, model in models.items()
                  if model['cached'] is None and not model['path'].with_suffix('.gltf').exists()]
        if failed:
            raise ValueError(f'{len(failed)} meshes failed conversion; see {args.work / "conversion.log"}: {failed}')
    return types, models, missing


def export(args):
    args.work.mkdir(parents=True, exist_ok=True)
    types, models, missing = prepare(args)
    if args.preflight:
        print(json.dumps(dict(candidateTypes=len(types) + len(missing), exportableTypes=len(types),
                              uniqueMeshes=len(models), cachedProfiles=sum(m['cached'] is not None for m in models.values()),
                              sourceMeshBytes=sum(m['size'] for m in models.values()), missingTypes=len(missing),
                              missingReasons=dict(collections.Counter(row['reason'] for row in missing))), indent=2))
        return
    paths = [str(model['path'].with_suffix('.gltf')) for _, model in sorted(models.items()) if model['cached'] is None]
    with concurrent.futures.ProcessPoolExecutor(max_workers=args.workers) as workers:
        profiles = list(workers.map(bake, paths, itertools.repeat(args.refresh)))
    profiles.extend(model['cached'] for model in models.values() if model['cached'] is not None)
    profiles.sort(key=lambda profile: profile['key'])
    for profile in profiles:
        source = models[profile['key']]
        profile.update(geometry=source['geometry'], md5=source['md5'], sha256=source['sha256'])
    result = dict(build=3662168, generator=GENERATOR, scope=args.scope, resourceIndexSha256=RESOURCE_INDEX_SHA256,
                  converter=dict(repository='https://github.com/neptuwunium/Knit',
                                 revision='30c0b48255bd3e8c845dd6de77aefded1eb7fd4e'),
                  types=types, profiles=profiles, missing=missing)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(result, separators=(',', ':'), allow_nan=False).encode()
    compressed = gzip.compress(encoded, mtime=0)
    manifest = dict(result)
    manifest['profiles'] = [dict((key, value) for key, value in profile.items() if key not in ('spheres', 'capsules'))
                            | dict(spheres=len(profile['spheres']), capsules=len(profile['capsules'])) for profile in profiles]
    manifest['payloadSha256'] = hashlib.sha256(compressed).hexdigest()
    args.manifest.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix('.tmp')
    temporary_manifest = args.manifest.with_suffix('.tmp')
    temporary.write_bytes(compressed)
    temporary_manifest.write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    # Commit the payload last, after both complete files have been staged.
    temporary_manifest.replace(args.manifest)
    temporary.replace(args.output)
    print(f'Baked {len(types)} types, {len(profiles)} models; {len(missing)} missing profiles; '
          f'{len(encoded)} raw bytes, {args.output.stat().st_size} compressed bytes')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', type=pathlib.Path, required=True)
    parser.add_argument('--client', type=pathlib.Path, required=True)
    parser.add_argument('--resources', type=pathlib.Path, required=True)
    parser.add_argument('--converter', type=pathlib.Path, required=True, help='Path to the external Knit.Convert.dll')
    parser.add_argument('--work', type=pathlib.Path, default=pathlib.Path('artifacts/static-collision-bake'))
    parser.add_argument('--output', type=pathlib.Path,
                        default=pathlib.Path('src/Eden.Data/Catalog/Flight/static-collision-3662168.json.gz'))
    parser.add_argument('--manifest', type=pathlib.Path, default=pathlib.Path('docs/protocol/static-collision-3662168.json'))
    parser.add_argument('--workers', type=int, default=2)
    parser.add_argument('--scope', choices=('structures', 'all-static'), default='structures')
    parser.add_argument('--preflight', action='store_true', help='Report coverage and required input size without converting meshes')
    parser.add_argument('--refresh', action='store_true', help='Recompute profiles instead of reusing the previous export')
    parser.add_argument('--types', type=int, nargs='*')
    export(parser.parse_args())
