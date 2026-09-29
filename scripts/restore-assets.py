#!/usr/bin/env python3
"""Собирает иконки и голос Джарвиса из assets/parts обратно в Resources."""
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
        print("restore: ресурсы уже на месте")
        return
    blobs = sorted(PARTS.glob("resources.tar.gz.b64.*"))
    if not blobs:
        raise SystemExit(f"нет {PARTS}/resources.tar.gz.b64.*")
    raw = base64.b64decode("".join(p.read_text(encoding="ascii") for p in blobs))
    DEST.mkdir(parents=True, exist_ok=True)
    with tarfile.open(fileobj=io.BytesIO(raw), mode="r:gz") as tar:
        tar.extractall(DEST)
    print(f"restore: распаковано в {DEST}")


if __name__ == "__main__":
    main()
