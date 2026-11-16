"""Eden's build-3662168 static skill adapter, compiled into a signed client archive."""
import json

# The package builder supplies this JSON literal before compiling the module.
DATA = json.loads(DATA_JSON)
_cache = {}


def record_client_error(stage, identity, exception_info=None):
    # Preserve the original exception; diagnostics must never change the client flow.
    try:
        import datetime
        import os
        import tempfile
        import traceback
        directory = os.path.join(os.environ.get('LOCALAPPDATA') or tempfile.gettempdir(), 'Eden', 'ClientErrors')
        if not os.path.isdir(directory):
            try:
                os.makedirs(directory)
            except OSError:
                if not os.path.isdir(directory):
                    return
        path = os.path.join(directory, 'character-selection-%s.log' % os.getpid())
        maximum_bytes = 2 * 1024 * 1024
        if os.path.exists(path) and os.path.getsize(path) >= maximum_bytes:
            return
        detail = traceback.format_exc() if exception_info is None else ''.join(traceback.format_exception(*exception_info))
        text = '%s UTC stage=%s character=%s\n%s\n' % (
            datetime.datetime.utcnow().isoformat(), stage, identity, detail)
        with open(path, 'ab') as output:
            output.write(text.encode('utf-8', 'replace'))
    except Exception:
        pass


class Row(object):
    def __init__(self, values, original=None):
        self._values = values
        self._original = original

    def __getattr__(self, name):
        if name in self._values:
            return self._values[name]
        if self._original is not None:
            return getattr(self._original, name)
        raise AttributeError(name)

    def __getitem__(self, name):
        return getattr(self, name)


class Vector(Row):
    def __len__(self):
        return 3

    def __iter__(self):
        return iter((self.x, self.y, self.z))

    def __getitem__(self, index):
        if isinstance(index, (int, long, slice)):
            return (self.x, self.y, self.z)[index]
        return Row.__getitem__(self, index)


def _convert(value, objects=False):
    if isinstance(value, list):
        return [_convert(item, objects) for item in value]
    if isinstance(value, dict):
        if objects:
            if set(value) == set(('x', 'y', 'z')):
                return Vector(value)
            return Row(dict((key, _convert(item, True)) for key, item in value.iteritems()))
        return dict((int(key), _convert(item)) for key, item in value.iteritems())
    return value


def apply(resource, original):
    name = resource.rsplit('/', 1)[-1].lower()
    edits = DATA['datasets'].get(name)
    if not edits:
        return original
    key = (name, id(original))
    if key not in _cache:
        result = dict(original.iteritems())
        for type_id, values in edits.iteritems():
            type_id = int(type_id)
            if name in ('types.fsdbinary', 'typedogma.fsdbinary'):
                converted = dict((field, _convert(value, True)) for field, value in values.iteritems())
                fallback = result.get(type_id)
                if fallback is None and name == 'types.fsdbinary':
                    fallback = original.get(DATA.get('templates', {}).get(str(type_id)))
                result[type_id] = Row(converted, fallback)
            else:
                result[type_id] = _convert(values)
        _cache[key] = result
    return _cache[key]


def message(message_id):
    return DATA['messages'].get(str(message_id))


class AlphaStorage(object):
    def __init__(self, original):
        self.original = original
        self.cache = {}

    def __getattr__(self, name):
        return getattr(self.original, name)

    def __getitem__(self, race):
        race = int(race)
        if race not in self.cache:
            row = self.original[race]
            edits = DATA['alpha'].get(str(race), {})
            skills = dict((skill.typeID, skill) for skill in row.skills)
            for type_id, level in edits.iteritems():
                skills[int(type_id)] = Row({'typeID': int(type_id), 'level': level})
            self.cache[race] = Row({'skills': skills.values(), 'skillsByTypeID': skills}, row)
        return self.cache[race]

    def get(self, race, default=None):
        try:
            return self[race]
        except KeyError:
            return default

    def iteritems(self):
        for race in self.original:
            yield race, self[race]


_alpha_storage = None


def alpha_storage(original):
    global _alpha_storage
    if _alpha_storage is None:
        _alpha_storage = AlphaStorage(original)
    return _alpha_storage


def _travel():
    return DATA.get('travel', {})


def _travel_cached(name, original, build):
    key = ('travel-' + name, id(original))
    if key not in _cache:
        _cache[key] = build()
    return _cache[key]


class MapRows(dict):
    def __init__(self, original):
        dict.__init__(self, original.iteritems())
        self._original = original

    def __getattr__(self, name):
        return getattr(self._original, name)

    def Get(self, identity):
        return self[identity]


def travel_jumps(original):
    edits = _travel().get('jumps')
    if not edits:
        return original
    return _travel_cached('jumps', original, lambda: list(original) + [_convert(row, True) for row in edits])


def travel_starmap(original):
    edits = _travel().get('jumps')
    if not edits:
        return original

    def build():
        result = dict(original)
        systems = result['solarSystems'] = dict(original['solarSystems'])
        for key, values in _travel().get('systems', {}).iteritems():
            identity = int(key)
            row = systems[identity] = dict(systems[identity])
            row['neighbours'] = [item['solarSystemID'] for item in values['neighbours']]
        jumps = result['jumps'] = list(original['jumps'])
        # Routing has two directed edges; both map renderers draw one line per pair.
        pairs = set(tuple(sorted((row['fromSystemID'], row['toSystemID']))) for row in jumps)
        for row in edits:
            source, destination = row['fromSystemID'], row['toSystemID']
            pair = tuple(sorted((source, destination)))
            if pair not in pairs:
                pairs.add(pair)
                jumps.append({'fromSystemID': source, 'toSystemID': destination, 'jumpType': row['jumpType'],
                              'fromCenter': systems[source]['center'], 'toCenter': systems[destination]['center']})
        return result
    return _travel_cached('starmap', original, build)


def travel_systems(original):
    edits = _travel().get('systems')
    if not edits:
        return original

    def build():
        result = MapRows(original)
        for key, values in edits.iteritems():
            identity = int(key)
            result[identity] = Row({'neighbours': _convert(values['neighbours'], True)}, original[identity])
        return result
    return _travel_cached('systems', original, build)


def travel_content(original):
    edits = _travel().get('content')
    if not edits:
        return original

    def build():
        result = MapRows(original)
        aggregate = getattr(original, 'stargates', None)
        if aggregate is not None:
            result.stargates = MapRows(aggregate)
        for key, values in edits.iteritems():
            identity = int(key)
            gates = MapRows(original[identity].stargates)
            for gate, value in values.iteritems():
                converted = _convert(value, True)
                converted._values['solarSystemID'] = identity
                gates[int(gate)] = converted
                if aggregate is not None:
                    result.stargates[int(gate)] = converted
            result[identity] = Row({'stargates': gates}, original[identity])
        return result
    return _travel_cached('content', original, build)


def _travel_location_values(identity, row):
    point = row['position']
    # Match mapObjects.db stargates: no orbital parent/index and connector=False.
    return {'locationID': identity, 'itemID': identity, 'locationName': row['name'], 'itemName': row['name'],
            'solarSystemID': row['systemID'], 'locationNameID': None, 'nameID': None,
            'typeID': row['typeID'], 'groupID': 10, 'orbitID': None, 'connector': False,
            'x': point['x'], 'y': point['y'], 'z': point['z'], 'celestialIndex': None, 'orbitIndex': None}


def travel_locations(original, keys):
    columns, rows = original
    edits = _travel().get('locations', {})
    for identity in keys:
        row = edits.get(str(identity))
        if row is not None:
            values = _travel_location_values(identity, row)
            # The location descriptor's solarSystemID field is the containing system.
            rows.InsertNew([values[column] for column in columns])
    return original


def travel_system_locations(original, system):
    for identity, row in _travel().get('locations', {}).iteritems():
        if row['systemID'] == system:
            values = _travel_location_values(int(identity), row)
            values['locationID'] = system
            original.InsertNew([values[column] for column in original.columns])
    return original
