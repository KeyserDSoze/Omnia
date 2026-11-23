#!/usr/bin/env python3
"""Validate Microsoft .NET base image manifest-index pins (no third-party deps).

Offline mode is fail-closed before NuGet restore. Remote mode inspects
the real registry with Docker buildx and proves index platforms/digest.
"""
import json
import re
import subprocess
import sys
from pathlib import Path

IMAGES = {
    "sdk": "mcr.microsoft.com/dotnet/sdk:10.0.404-noble",
    "runtime": "mcr.microsoft.com/dotnet/aspnet:10.0.15-noble",
}
INDEX_TYPES = {
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
}
MANIFEST_TYPES = {
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
}
PLATFORMS = {"linux/amd64", "linux/arm64"}
DIGEST = re.compile(r"sha256:[0-9a-f]{64}\Z")


def extract_pins(root: Path):
    path = root / "docker/Dockerfile.api"
    try:
        dockerfile = path.read_text(encoding="utf-8")
    except OSError:
        return {}, [f"{path}: cannot read Dockerfile"]
    refs = re.findall(r"(?m)^FROM\s+(\S+)", dockerfile)
    if len(refs) != 2:
        return {}, ["Dockerfile.api: exactly two pinned Microsoft stages required"]
    pins = {}
    errors = []
    for (role, tag), ref in zip(IMAGES.items(), refs):
        prefix = tag + "@"
        if not ref.startswith(prefix):
            errors.append(f"Dockerfile.api: {role} must pin {tag}@sha256:<digest>")
            continue
        digest = ref[len(prefix):]
        if not DIGEST.fullmatch(digest):
            errors.append(f"Dockerfile.api: {role} needs a valid SHA-256 digest")
        else:
            pins[role] = (tag, digest)
    return pins, errors


def verify_index(index: object, expected_digest: str | None = None) -> list[str]:
    if not isinstance(index, dict):
        return ["manifest index must be a JSON object"]
    failures = []
    if index.get("mediaType") not in INDEX_TYPES:
        failures.append("top-level OCI/Docker manifest index required, not a platform image")
    digest = index.get("digest")
    if not isinstance(digest, str) or not DIGEST.fullmatch(digest):
        failures.append("manifest index metadata has invalid digest")
    elif expected_digest is not None and digest != expected_digest:
        failures.append("registry manifest index digest differs from committed pin")
    entries = index.get("manifests")
    if not isinstance(entries, list) or not entries:
        return failures + ["manifest index has no entries"]
    supported = set()
    for entry in entries:
        if not isinstance(entry, dict):
            failures.append("invalid manifest descriptor")
            continue
        platform = entry.get("platform")
        if not isinstance(platform, dict):
            continue
        target = f"{platform.get('os')}/{platform.get('architecture')}"
        if target in PLATFORMS:
            if entry.get("mediaType") not in MANIFEST_TYPES:
                failures.append(f"{target} must refer to an image manifest")
            if not isinstance(entry.get("digest"), str) or not DIGEST.fullmatch(entry["digest"]):
                failures.append(f"{target} image manifest digest invalid")
            else:
                supported.add(target)
    for target in sorted(PLATFORMS - supported):
        failures.append(f"missing supported platform {target}")
    return failures


def inspect_manifest(ref: str) -> dict:
    result = subprocess.run(
        ["docker", "buildx", "imagetools", "inspect", "--format",
         "{{json .Manifest}}", ref],
        check=True, capture_output=True, text=True, timeout=160
    )
    return json.loads(result.stdout)


def verify_registry(pins: dict, require_tag_match: bool):
    failures = []
    for role, (tag, pinned) in pins.items():
        try:
            by_digest = inspect_manifest(tag + "@" + pinned)
            errs = verify_index(by_digest, pinned)
            if errs:
                failures.extend(f"{role}: {error}" for error in errs)
            if require_tag_match:
                live_tag = inspect_manifest(tag)
                errors = verify_index(live_tag, pinned)
                failures.extend(f"{role} published tag: {error}" for error in errors)
            if not errs:
                variants = [f"{d['platform']['os']}/{d['platform']['architecture']}={d['digest']}"
                            for d in by_digest["manifests"] if isinstance(d, dict)
                            and isinstance(d.get("platform"), dict)
                            and f"{d['platform'].get('os')}/{d['platform'].get('architecture')}" in PLATFORMS]
                print(f"OCI VERIFIED {role}: {tag}@{pinned}; " + "; ".join(variants))
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired,
                json.JSONDecodeError, OSError, KeyError) as exc:
            failures.append(f"{role}: cannot independently inspect image index ({exc})")
    return failures


def discover_official_images():
    failures = []
    for role, tag in IMAGES.items():
        try:
            info = inspect_manifest(tag)
            issues = verify_index(info)
            failures.extend(f"{role}: {issue}" for issue in issues)
            if not issues:
                platforms = sorted({
                    f"{m['platform']['os']}/{m['platform']['architecture']}"
                    for m in info["manifests"]
                    if isinstance(m, dict) and isinstance(m.get("platform"), dict)
                    and f"{m['platform'].get('os')}/{m['platform'].get('architecture')}" in PLATFORMS
                })
                print(f"OCI_INDEX {role} {tag}@{info['digest']} platforms={','.join(platforms)}")
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired,
                json.JSONDecodeError, OSError, KeyError) as exc:
            failures.append(f"{role}: official manifest registry query failed ({exc})")
    return failures


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "--offline"
    root = Path(__file__).resolve().parents[2]
    if mode == "--discover":
        issues = discover_official_images()
    elif mode in ("--offline", "--remote"):
        pins, issues = extract_pins(root)
        if not issues and mode == "--remote":
            issues = verify_registry(pins, require_tag_match=True)
    else:
        print("Usage: --discover | --offline | --remote", file=sys.stderr)
        return 2
    if issues:
        print("Microsoft .NET OCI image pin policy FAILED:", file=sys.stderr)
        for issue in issues:
            print(" - " + issue, file=sys.stderr)
        return 1
    print("Microsoft .NET OCI image pin policy PASS (" + mode + ")")
    return 0


if __name__ == "__main__":
    sys.exit(main())
