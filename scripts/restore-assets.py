#!/usr/bin/env python3
"""Кладёт иконки из assets/parts в Resources перед сборкой."""
from __future__ import annotations

import base64
import io
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PARTS = ROOT / "assets" / "parts"
DEST = ROOT / "src" / "KovchegVPN" / "Resources"
MARKER = DEST / "kovcheg.ico"


def main() -> None:
    if MARKER.is_file() and MARKER.stat().st_size > 1000:
        print("restore: иконки уже на месте")
        return
    blobs = sorted(PARTS.glob("icons.b64.*"))
    if not blobs:
        raise SystemExit(f"нет {PARTS}/icons.b64.*")
    raw = base64.b64decode("".join(p.read_text(encoding="ascii") for p in blobs))
    DEST.mkdir(parents=True, exist_ok=True)
    with tarfile.open(fileobj=io.BytesIO(raw), mode="r:gz") as tar:
        try:
            tar.extractall(DEST, filter="data")
        except TypeError:
            tar.extractall(DEST)
    print(f"restore: иконки в {DEST}")


if __name__ == "__main__":
    main()
