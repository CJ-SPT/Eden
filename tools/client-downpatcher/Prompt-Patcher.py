"""Collect the SharedCache and destination paths for the portable Windows package."""

from pathlib import Path
from runpy import run_path
import sys


def ask_folder(prompt):
    while True:
        answer = input(prompt).strip().strip('"')
        if answer and Path(answer).is_absolute():
            return answer
        print(r"Enter a full Windows folder path, such as D:\EdenClient.")


def main():
    print("Use the live SharedCache containing tq and ResFiles.")
    print("Choose a separate destination with about 46 GiB free.")
    try:
        source = ask_folder("Live SharedCache folder: ")
        destination = ask_folder("Eden client destination: ")
    except (EOFError, KeyboardInterrupt):
        print("\nSetup canceled.")
        return 130

    patcher = str(Path(__file__).with_name("Downpatch-Client.py").resolve())
    sys.argv = [patcher, "--source", source, "--destination", destination]
    run_path(patcher, run_name="__main__")
    return 0


if __name__ == "__main__":
    sys.exit(main())
