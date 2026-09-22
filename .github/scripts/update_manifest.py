#!/usr/bin/env python3
"""Add a released version to manifest.json (Jellyfin plugin repository format)."""
import datetime
import hashlib
import json
import sys

REPO = "https://github.com/qDgey/jellyfin-plugin-aniliberty"
TARGET_ABI = "12.1.0.0"

version, zip_path, changelog = sys.argv[1], sys.argv[2], sys.argv[3]
with open(zip_path, "rb") as f:
    checksum = hashlib.md5(f.read()).hexdigest()

with open("manifest.json", encoding="utf-8") as f:
    manifest = json.load(f)

plugin = manifest[0]
plugin["versions"] = [v for v in plugin["versions"] if v["version"] != version]
plugin["versions"].insert(0, {
    "version": version,
    "changelog": changelog,
    "targetAbi": TARGET_ABI,
    "sourceUrl": f"{REPO}/releases/download/v{version}/{zip_path.split('/')[-1]}",
    "checksum": checksum,
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
})

with open("manifest.json", "w", encoding="utf-8") as f:
    json.dump(manifest, f, ensure_ascii=False, indent=2)
    f.write("\n")
