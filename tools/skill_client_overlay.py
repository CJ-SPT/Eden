"""Eden's build-3662168 static skill adapter, compiled into a signed client archive."""
import json

# The package builder supplies this JSON literal before compiling the module.
DATA = json.loads(DATA_JSON)
_cache = {}


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


def _convert(value, objects=False):
    if isinstance(value, list):
        return [_convert(item, objects) for item in value]
    if isinstance(value, dict):
        if objects:
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
