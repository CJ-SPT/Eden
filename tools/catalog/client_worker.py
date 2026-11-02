"""Run inside the pinned client's Stackless Python 2.7 interpreter."""
import json
import sys


def plain(value):
    if value is None or isinstance(value, (bool, int, long, float, basestring)):
        return value
    # The legacy object loader implements dynamic attribute access even for
    # missing fields. Its schema distinguishes fields from loader internals.
    if type(value).__name__ == 'VectorLoader':
        return {name: value[index] for name, index in value.schema['aliases'].items()}
    if type(value).__module__.endswith('objectLoader'):
        result = {}
        for field in value.__schema__['attributes']:
            try:
                result[field] = plain(value[field])
            except (AttributeError, KeyError):
                pass
        return result
    if hasattr(type(value), 'iteritems'):
        return {key: plain(item) for key, item in value.iteritems()}
    if hasattr(type(value), '__iter__') or isinstance(value, (tuple, list)):
        return [plain(item) for item in value]
    return {field: plain(getattr(value, field)) for field in dir(value)
            if not field.startswith('_') and not callable(getattr(value, field))}


def decode(task):
    name, source, schema, loader, output = task
    if loader:
        data = __import__(loader).load(source)
    else:
        from fsd.schemas import binaryLoader, persistence
        definition = persistence.LoadSchema(open(schema, 'rb')) if schema else None
        with open(source, 'rb') as stream:
            data = binaryLoader.LoadFromString(stream.read(), definition)
    if hasattr(type(data), 'iteritems'):
        pairs = data.iteritems()
    elif hasattr(type(data), '__iter__'):
        pairs = enumerate(data)
    else:
        pairs = [('root', data)]
    with open(output, 'wb') as writer:
        for key, value in pairs:
            writer.write(json.dumps([key, plain(value)], separators=(',', ':'),
                                    allow_nan=False) + '\n')


failed = []
for task in json.load(open(job_path, 'rb')):
    try:
        decode(task)
    except Exception:
        import traceback
        traceback.print_exc()
        print >>sys.stderr, 'Failed to decode ' + task[0]
        failed.append(task[0])


if failed:
    raise RuntimeError(repr(failed))
