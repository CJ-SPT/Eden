"""Host the staged 64-bit client interpreter in a dedicated setup process."""
import ctypes
import os
from pathlib import Path
import sys
import zipfile
import zlib


def main(root, jobs):
    binaries = root / "tq/bin64"
    library = jobs.parent / "client-python.zip"
    with zipfile.ZipFile(root / "tq/code.ccp") as source, zipfile.ZipFile(library, "w") as target:
        for entry in dict.fromkeys(source.namelist()):
            if entry.endswith(".pyj"):
                target.writestr(entry[:-1] + "c", zlib.decompress(source.read(entry)))

    os.chdir(binaries)
    with os.add_dll_directory(str(binaries)):
        runtime = ctypes.CDLL(str(binaries / "python27.dll"))
        ctypes.c_int.in_dll(runtime, "Py_NoSiteFlag").value = 1
        runtime.Py_InitializeEx(0)
        worker = Path(__file__).with_name("client_worker.py").resolve()
        setup = ("import sys\nsys.path[:] = %r\njob_path = %r\nexecfile(%r)" %
                 ([str(library), str(binaries)], str(jobs), str(worker)))
        if runtime.PyRun_SimpleString(setup.encode("ascii", "backslashreplace")):
            raise SystemExit(1)


if __name__ == "__main__":
    main(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
