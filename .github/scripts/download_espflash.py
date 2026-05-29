#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import sys
import urllib.request
import zipfile
from pathlib import Path

ASSET_NAME = "espflash-x86_64-pc-windows-msvc.zip"
API_ROOT = "https://api.github.com/repos/esp-rs/espflash/releases"


def http_json(url: str) -> dict:
    request = urllib.request.Request(url, headers={"Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.loads(response.read().decode("utf-8"))


def download(url: str, out: Path) -> None:
    request = urllib.request.Request(url, headers={"Accept": "application/octet-stream"})
    with urllib.request.urlopen(request, timeout=120) as response:
        out.write_bytes(response.read())


def main() -> int:
    parser = argparse.ArgumentParser(description="Download espflash.exe for OpenFinger release packages")
    parser.add_argument("--version", default="", help="espflash release tag; default uses latest")
    parser.add_argument("--destination", default="src/OpenFinger.Control/FirmwareTools")
    args = parser.parse_args()

    destination = Path(args.destination)
    destination.mkdir(parents=True, exist_ok=True)

    release_url = f"{API_ROOT}/tags/{args.version}" if args.version else f"{API_ROOT}/latest"
    release = http_json(release_url)
    assets = release.get("assets", [])
    asset = next((item for item in assets if item.get("name") == ASSET_NAME), None)
    if asset is None:
        names = ", ".join(item.get("name", "<unnamed>") for item in assets)
        raise RuntimeError(f"release asset not found: {ASSET_NAME}; available: {names}")

    archive = destination / ASSET_NAME
    download(asset["browser_download_url"], archive)

    with zipfile.ZipFile(archive) as zf:
        zf.extractall(destination)
    archive.unlink()

    espflash = destination / "espflash.exe"
    if not espflash.exists() or espflash.stat().st_size == 0:
        raise RuntimeError("downloaded espflash archive did not contain espflash.exe")

    print(f"bundled {espflash}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
