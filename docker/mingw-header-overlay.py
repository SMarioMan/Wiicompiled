"""Expose MinGW headers with Windows case-insensitive lookup on Linux."""
import json
from pathlib import Path
import sys


def directory(path, *, root=False):
    return {
        "type": "directory",
        "name": str(path) if root else path.name,
        "contents": [
            directory(child) if child.is_dir() else {
                "type": "file", "name": child.name,
                "external-contents": str(child),
            }
            for child in sorted(path.iterdir())
        ],
    }


if __name__ == "__main__":
    # Keep the target-triplet alias: clang searches it even when MinGW's
    # include directory is a symlink to generic-w64-mingw32/include.
    headers = Path(sys.argv[1]).absolute()
    if not headers.is_dir():
        raise ValueError(f"Header directory does not exist: {headers}")
    overlay = {"version": 0, "case-sensitive": False, "roots": [directory(headers, root=True)]}
    Path(sys.argv[2]).write_text(json.dumps(overlay), encoding="utf-8")
