#!/usr/bin/env python3
"""Подставляет client.env в копию исходников. Дерево git не трогает, если передан --root."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

TEXT_SUFFIXES = {".cs", ".json", ".xaml", ".csproj", ".md"}


def load_env(path: Path) -> dict[str, str]:
    out: dict[str, str] = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        out[key.strip()] = value.strip()
    return out


def require(env: dict[str, str], key: str) -> str:
    value = env.get(key, "").strip()
    if not value:
        sys.exit(f"в client.env нет {key}")
    return value


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("env_file", type=Path)
    parser.add_argument("--root", type=Path, required=True, help="копия src, которую можно испортить ключами")
    args = parser.parse_args()
    env = load_env(args.env_file)

    ip = require(env, "SERVER_IP")
    ip6 = env.get("SERVER_IP6", "none").strip() or "none"
    ip6_prefix = env.get("SERVER_IP6_PREFIX", "").strip()
    if ip6 == "none":
        ip6 = "2001:db8::10"
        ip6_prefix = "2001:db8:ffff:"
    if not ip6_prefix:
        sys.exit("в client.env нет SERVER_IP6_PREFIX")

    pairs = [
        ("203.0.113.10", ip),
        ("2001:db8::10", ip6),
        ("2001:db8:ffff:", ip6_prefix),
        ("REPLACE_SS_PASSWORD", require(env, "SS_PASSWORD")),
        ("00000000-0000-4000-8000-000000000001", require(env, "UUID")),
        ("REPLACE_REALITY_PUB", require(env, "REALITY_PUB")),
        ("0011223344556677", require(env, "REALITY_SID")),
        ("/replacepath", require(env, "XHTTP_PATH")),
        ("REPLACE_MTG_SECRET", require(env, "MTG_SECRET")),
        ("REPLACE_OTA_PUB", require(env, "OTA_PUB")),
        ("REPLACE_COUNTRY", require(env, "COUNTRY")),
        ("REPLACE_ISO", require(env, "COUNTRY_ISO")),
        ("REPLACE_BANNER", require(env, "COUNTRY_BANNER")),
    ]
    root = args.root
    if not root.is_dir():
        sys.exit(f"нет каталога {root}")

    changed = 0
    for path in root.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in TEXT_SUFFIXES:
            continue
        text = path.read_text(encoding="utf-8")
        new = text
        for old, value in pairs:
            new = new.replace(old, value)
        if new != text:
            path.write_text(new, encoding="utf-8")
            changed += 1
    marker = root / "KovchegVPN" / "Cfg.cs"
    if marker.is_file() and "203.0.113.10" in marker.read_text(encoding="utf-8"):
        sys.exit("Cfg.cs всё ещё с заглушкой IP — stamp не попал в исходники")
    print(f"stamp: обновлено файлов {changed}, IP {ip}")


if __name__ == "__main__":
    main()
