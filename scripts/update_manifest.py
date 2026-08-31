#!/usr/bin/env python3
"""Add or replace a version entry in the Jellyfin plugin repository manifest.

Plugin identity (name, guid, target ABI, ...) is read from build.yaml so it is
never duplicated here. The version and download URL are supplied by the release
workflow, which derives them from the git tag and the repository it runs in.
"""

import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

import yaml

CHUNK_SIZE = 1024 * 1024


def md5_of(path: Path) -> str:
    """Return the lowercase hex MD5 of a file; Jellyfin verifies this before install."""
    digest = hashlib.md5()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(CHUNK_SIZE), b""):
            digest.update(chunk)
    return digest.hexdigest()


def version_key(value: str) -> tuple:
    """Sort key for a dotted version string, so the newest entry stays first."""
    parts = []
    for part in str(value).split("."):
        parts.append(int(part) if part.isdigit() else 0)
    return tuple(parts)


def build_package(meta: dict, versions: list) -> dict:
    """Shape a PackageInfo entry exactly as MediaBrowser.Model.Updates expects."""
    package = {
        "guid": str(meta["guid"]),
        "name": meta["name"],
        "description": (meta.get("description") or "").strip(),
        "overview": (meta.get("overview") or "").strip(),
        "owner": meta.get("owner", ""),
        "category": meta.get("category", "General"),
        "versions": versions,
    }
    # Only emit imageUrl when there is one, matching the official repo's shape.
    if meta.get("imageUrl"):
        package["imageUrl"] = meta["imageUrl"]
    return package


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-config", default="build.yaml")
    parser.add_argument("--manifest", default="manifest.json")
    parser.add_argument("--zip", required=True, help="Built plugin zip to checksum.")
    parser.add_argument("--version", required=True, help="Version being released.")
    parser.add_argument("--source-url", required=True, help="Public download URL of the zip.")
    parser.add_argument("--changelog", default="")
    args = parser.parse_args()

    build_config = Path(args.build_config)
    zip_path = Path(args.zip)
    manifest_path = Path(args.manifest)

    if not zip_path.is_file():
        print(f"error: zip not found: {zip_path}", file=sys.stderr)
        return 1

    meta = yaml.safe_load(build_config.read_text(encoding="utf-8"))

    entry = {
        "version": args.version,
        "changelog": args.changelog.strip(),
        "targetAbi": str(meta["targetAbi"]),
        "sourceUrl": args.source_url,
        "checksum": md5_of(zip_path),
        # ISO 8601 UTC, as required by the manifest schema.
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    manifest = []
    if manifest_path.is_file():
        content = manifest_path.read_text(encoding="utf-8").strip()
        if content:
            manifest = json.loads(content)

    guid = str(meta["guid"])
    index = next(
        (i for i, p in enumerate(manifest) if str(p.get("guid", "")).lower() == guid.lower()),
        None,
    )
    existing_versions = manifest[index].get("versions", []) if index is not None else []

    # Rebuild the entry outright rather than merging, so renames and dropped
    # fields propagate instead of lingering from the previous release.
    package = build_package(meta, existing_versions)
    if index is None:
        manifest.append(package)
    else:
        manifest[index] = package

    versions = [v for v in package["versions"] if v.get("version") != args.version]
    versions.append(entry)
    versions.sort(key=lambda v: version_key(v.get("version", "0")), reverse=True)
    package["versions"] = versions

    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    # Keep build.yaml's version in step with what was actually published, without
    # reserialising the file and losing its comments and formatting.
    raw = build_config.read_text(encoding="utf-8")
    updated = re.sub(
        r'^version:\s*.*$',
        f'version: "{args.version}"',
        raw,
        count=1,
        flags=re.MULTILINE,
    )
    if updated != raw:
        build_config.write_text(updated, encoding="utf-8")

    print(f"{manifest_path}: {meta['name']} {args.version} ({entry['checksum']})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
