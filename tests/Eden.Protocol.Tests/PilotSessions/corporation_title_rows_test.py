"""Headless pinned Row/title methods; no Blue DLL, module imports, UI or session.

The C# test validates wire tags and supplies decoded state. This checks Python 2
allocation and actual pinned methods, not a full native transport decode.
"""
import ctypes
import hashlib
import json
import marshal
import os
from pathlib import Path
import sys
import zipfile
import zlib

rows = marshal.dumps(json.loads(Path(sys.argv[1]).read_text(encoding='utf-8')), 2)
root = Path(sys.argv[2])
binaries = root / 'tq/bin64'
runtime_path = binaries / 'python27.dll'
assert hashlib.sha256(runtime_path.read_bytes()).hexdigest() == (
    '1266e119d5db70281831f06049faa2694e658a68d504e0cc5680ec5726ba3aa2')
archive = root / '.eden-original-code.ccp'
assert hashlib.sha256(archive.read_bytes()).hexdigest() == (
    '37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368')
with zipfile.ZipFile(archive) as source:
    row_blob = zlib.decompress(source.read('carbon/common/script/sys/row.pyj'))
    titles_blob = zlib.decompress(source.read('eve/client/script/ui/services/corporation/bco_titles.pyj'))

bootstrap = 'ROW_BLOB = %r\nTITLES_BLOB = %r\nROWS = %r\n' % (row_blob, titles_blob, rows)
bootstrap += r'''
import marshal, sys

class Value(object):
    def __init__(self, **fields):
        self.__dict__.update(fields)

def walk(code):
    yield code
    for child in code.co_consts:
        if hasattr(child, 'co_code'):
            for nested in walk(child):
                yield nested

row_code = next(c for c in walk(marshal.loads(ROW_BLOB[8:])) if c.co_name == 'Row')
scope = {'__name__': 'carbon.common.script.sys.row', 'sys': sys, '__builtins__': __builtins__}
# Execute only the class definition and its data methods, never its module body.
Row = type('Row', (object,), type(lambda: None)(row_code, scope)())
assert Row.__slots__ == ('header', 'line')
assert isinstance(Row, type)
'''

checks = r'''
titles = {}
for state in marshal.loads(ROWS):
    # Carbon NEWOBJ invokes __new__ followed by __setstate__.
    row = Row.__new__(Row)
    row.__setstate__((state['Header'], state['Line']))
    assert row.roles == 1L << 40
    titles[row.titleID] = row
assert len(titles) == 16
assert titles[1].titleName == u'Explorers \u661f'
assert titles[2].titleName == u''

calls = [0]
def read_titles():
    calls[0] += 1
    return titles

method_code = next(c for c in walk(marshal.loads(TITLES_BLOB[8:])) if c.co_name == 'GetTitles')
get_titles = type(lambda: None)(method_code, {
    '__builtins__': __builtins__,
    'math': Value(log=lambda value, base: value.bit_length() - 1),
    'localization': Value(GetByLabel=lambda label, index: u'Untitled %s' % index)})
service = Value(titles=None, GetCorpRegistry=lambda: Value(GetTitles=read_titles))
for _ in range(2):
    assert get_titles(service) is titles
assert calls[0] == 1
assert titles[1].titleName == u'Explorers \u661f'
for index in range(1, 16):
    assert titles[1 << index].titleName == u'Untitled %s' % (index + 1)
    assert titles[1 << index].roles == 1L << 40
titles[1].titleName = u'Renamed'
assert titles[1].line[titles[1].header.index('titleName')] == u'Renamed'
print('Native corporation title assertions passed')
'''

with os.add_dll_directory(str(binaries)):
    runtime = ctypes.CDLL(str(runtime_path))
    ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
    runtime.Py_InitializeEx(0)
    assert runtime.PyRun_SimpleString(bootstrap.encode('ascii')) == 0
    runtime.PyImport_AddModule.restype = ctypes.c_void_p
    runtime.PyImport_AddModule.argtypes = [ctypes.c_char_p]
    runtime.PyObject_GetAttrString.restype = ctypes.c_void_p
    runtime.PyObject_GetAttrString.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
    runtime.PyInstance_NewRaw.restype = ctypes.c_void_p
    runtime.PyInstance_NewRaw.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    runtime.PyErr_ExceptionMatches.argtypes = [ctypes.c_void_p]
    native_row = runtime.PyObject_GetAttrString(runtime.PyImport_AddModule(b'__main__'), b'Row')
    assert native_row
    # Legacy INSTANCE uses old-style allocation; it cannot instantiate this Row.
    assert not runtime.PyInstance_NewRaw(native_row, None)
    # PyInstance_NewRaw reports a bad internal call for a non-PyClass object.
    assert runtime.PyErr_ExceptionMatches(ctypes.c_void_p.in_dll(runtime, 'PyExc_SystemError').value)
    runtime.PyErr_Clear()
    assert runtime.PyRun_SimpleString(checks.encode('ascii')) == 0
