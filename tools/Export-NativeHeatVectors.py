"""Evaluate only the pinned client's inert scalar heat function; never launch a client."""
import argparse
import ast
import ctypes
import hashlib
import json
import os
from pathlib import Path
import tempfile
import zipfile
import zlib


ARCHIVE_SHA256 = '37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368'


def export(root, output):
    root, output = root.resolve(), output.resolve()
    if output.is_relative_to(root):
        raise ValueError('The output must stay outside the client installation')
    archive = root / 'tq/code.ccp'
    if hashlib.sha256(archive.read_bytes()).hexdigest().upper() != ARCHIVE_SHA256:
        raise ValueError('Expected the pinned build-3662168 client archive')
    cases = [
        (0, 0, 0, .01, 1, 100),
        (20, 0, .01, .01, 1, 100),
        (0, 10000, .01, .01, 1, 100),
        (20, 10000, .01, .01, 1, 100),
        (50, 10000, 0, .01, 1, 100),
        (.5, 0, 0, .01, 1, 100),
        (.499999, 0, 0, .01, 1, 100),
        (.5, 1, 0, .01, 1, 100),
        (20, 1000, 4.99999e-8, .01, 1, 100),
        (20, 1000, 5e-8, .01, 1, 100),
        (7, 12.5, .07, .02, 2.5, 75),
        (150, 10000, .02, .01, 1, 100),
        (2, 1000000, 0, .01, 1, 100),
    ]
    with zipfile.ZipFile(archive) as source:
        code = zlib.decompress(source.read('dogma/attributes/heatAttribute.pyj'))
    binaries = root / 'tq/bin64'
    previous = Path.cwd()
    try:
        os.chdir(binaries)
        with tempfile.TemporaryDirectory(prefix='eden-native-heat-') as directory:
            extracted = Path(directory) / 'heat.pyc'
            result = Path(directory) / 'result.txt'
            extracted.write_bytes(code)
            # No client imports or module-level code execute. Construct only
            # CalculateHeat from its inert code object and supply math/round.
            script = (
                "import sys,marshal\nsys.path[:]=%r\nimport math\n"
                "f=open(%r,'rb');f.read(8);root=marshal.load(f);f.close()\n"
                "code=next(c for c in root.co_consts if getattr(c,'co_name',None)=='CalculateHeat')\n"
                "calculate=(lambda:None).__class__(code,{'exp':math.exp,'round':round})\n"
                "open(%r,'wb').write(repr([calculate(*v) for v in %r]))\n"
            ) % ([str(binaries)], str(extracted), str(result), cases)
            with os.add_dll_directory(str(binaries)):
                runtime = ctypes.CDLL(str(binaries / 'python27.dll'))
                ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
                runtime.Py_InitializeEx(0)
                if runtime.PyRun_SimpleString(script.encode('ascii', 'backslashreplace')):
                    raise RuntimeError('Inert native heat calculation failed')
            results = ast.literal_eval(result.read_text(encoding='ascii'))
    finally:
        os.chdir(previous)
    vectors = [dict(heat=v[0], milliseconds=v[1], incoming=v[2], dissipation=v[3], generation=v[4],
                    capacity=v[5], expected=value) for v, value in zip(cases, results)]
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(dict(build=3662168, archiveSha256=ARCHIVE_SHA256, vectors=vectors), indent=2) + '\n',
                      encoding='utf-8')
    print('Exported', len(vectors), 'inert native heat vectors to', output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--development-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/HeatReferenceVectors.json'))
    args = parser.parse_args()
    export(args.development_root, args.output)
