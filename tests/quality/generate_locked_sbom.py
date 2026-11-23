#!/usr/bin/env python3
"""Deterministic CycloneDX 1.6 dependency inventories from reviewed lockfiles.

This is an inventory of locked npm and NuGet packages, NOT a scanned
binary/container SBOM, SCA verdict, resolved dependency edge graph or
signed provenance. Requires only the Python standard library.
"""
import argparse
import base64
import binascii
import json
import sys
from pathlib import Path
from urllib.parse import quote
from xml.etree import ElementTree

SCOPE = ("src/backend", "tests")


def load_json(path: Path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, ValueError) as exc:
        raise ValueError(f"{path}: missing or invalid lock/manifest JSON ({exc})") from exc


def sha512_hex(value, origin: str, npm: bool = False):
    if npm:
        if not isinstance(value, str) or not value.startswith("sha512-"):
            raise ValueError(f"{origin}: missing SHA-512 npm integrity")
        value = value[len("sha512-"):]
    if not isinstance(value, str):
        raise ValueError(f"{origin}: missing SHA-512 content hash")
    try:
        raw = base64.b64decode(value, validate=True)
    except (binascii.Error, ValueError) as exc:
        raise ValueError(f"{origin}: invalid SHA-512 content hash") from exc
    if len(raw) != 64 or base64.b64encode(raw).decode("ascii") != value:
        raise ValueError(f"{origin}: invalid SHA-512 content hash")
    return raw.hex()


def npm_purl(name: str, version: str):
    if not isinstance(name, str) or not name or not isinstance(version, str) or not version:
        raise ValueError("npm package name/version missing")
    if name.startswith("@"):
        if len(name.split("/")) != 2 or not all(name.split("/")):
            raise ValueError(f"invalid scoped npm package {name}")
        parts = name.split("/")
        escaped = "/".join(quote(p, safe="") for p in parts)
    else:
        if "/" in name:
            raise ValueError(f"invalid unscoped npm package {name}")
        escaped = quote(name, safe="")
    return f"pkg:npm/{escaped}@{quote(version, safe='')}"


def add_component(components: dict, owners: dict, purl: str,
                  name: str, version: str, sha512: str, owner: str):
    old = components.get(purl)
    if old is not None:
        if old["hashes"][0]["content"] != sha512:
            raise ValueError(f"conflicting SHA-512 for identical package {purl}")
    else:
        components[purl] = {
            "bom-ref": purl, "type": "library", "name": name,
            "version": version, "purl": purl,
            "hashes": [{"alg": "SHA-512", "content": sha512}]
        }
    owners.setdefault(purl, set()).add(owner)


def bom_for(name: str, version: str | None, components: dict,
            owners: dict, source_property: str, source_value: str,
            entry_count: int):
    metadata_component = {"type": "application", "name": name}
    if version is not None:
        metadata_component["version"] = version
    result = {
        "bomFormat": "CycloneDX", "specVersion": "1.6", "version": 1,
        "metadata": {
            "component": metadata_component,
            "properties": [
                {"name": source_property, "value": source_value},
                {"name": "omnia:locked-package-occurrences", "value": str(entry_count)},
                {"name": "omnia:unique-packages", "value": str(len(components))}
            ]
        },
        "components": []
    }
    for purl in sorted(components):
        component = dict(components[purl])
        component["properties"] = [{
            "name": "omnia:lock-owners",
            "value": ";".join(sorted(owners[purl]))
        }]
        result["components"].append(component)
    return result


def npm_bom(root: Path):
    package_dir = root / "src/mobile"
    manifest = load_json(package_dir / "package.json")
    lock = load_json(package_dir / "package-lock.json")
    if lock.get("lockfileVersion") != 3 or not isinstance(lock.get("packages"), dict):
        raise ValueError("src/mobile/package-lock.json: npm lockfile v3 required")
    packages = lock["packages"]
    meta = packages.get("")
    if not isinstance(meta, dict):
        raise ValueError("src/mobile/package-lock.json: missing root package")
    for field in ("name", "version"):
        if lock.get(field) != manifest.get(field) or meta.get(field) != manifest.get(field):
            raise ValueError(f"src/mobile/package-lock.json: root {field} mismatch")
    for field in ("dependencies", "devDependencies"):
        if (meta.get(field) or {}) != (manifest.get(field) or {}):
            raise ValueError(f"src/mobile/package-lock.json: root {field} mismatch")
    for field in ("dependencies", "devDependencies"):
        for name in (manifest.get(field) or {}):
            if "node_modules/" + name not in packages:
                raise ValueError(f"npm direct dependency missing locked package: {name}")

    components, owners = {}, {}
    entry_count = 0
    for path, pkg in sorted(packages.items()):
        if path == "":
            continue
        if not isinstance(pkg, dict) or not path.startswith("node_modules/"):
            raise ValueError(f"npm lock: invalid package entry {path}")
        name = pkg.get("name") or path.rsplit("node_modules/", 1)[-1]
        version = pkg.get("version")
        if not isinstance(version, str) or not version:
            raise ValueError(f"npm lock: unresolved package version for {path}")
        if not isinstance(pkg.get("resolved"), str) or not pkg["resolved"].startswith(
                "https://registry.npmjs.org/"):
            raise ValueError(f"npm lock: package outside official registry {path}")
        hash_value = sha512_hex(pkg.get("integrity"), path, npm=True)
        add_component(components, owners, npm_purl(name, version),
                      name, version, hash_value, path)
        entry_count += 1
    if not components:
        raise ValueError("npm lock: no dependency packages")
    return bom_for(manifest["name"], manifest["version"], components, owners,
                   "omnia:npm:lockfile", "src/mobile/package-lock.json", entry_count)


def project_paths(root: Path):
    projects = sorted(p for relative in SCOPE for p in (root / relative).rglob("*.csproj")
                      if "obj" not in p.parts and "bin" not in p.parts)
    if not projects:
        raise ValueError("NuGet: no project files under src/backend and tests")
    return projects


def nuget_bom(root: Path):
    components, owners = {}, {}
    entry_count = 0
    projects = project_paths(root)
    for project in projects:
        rel = project.relative_to(root).as_posix()
        try:
            xml = ElementTree.parse(project).getroot()
        except (ElementTree.ParseError, OSError) as exc:
            raise ValueError(f"{rel}: invalid csproj ({exc})") from exc
        lock_path = project.with_name("packages.lock.json")
        lock = load_json(lock_path)
        targets = lock.get("dependencies")
        if lock.get("version") not in (1, 2, 3) or not isinstance(targets, dict) or not targets:
            raise ValueError(f"{rel}: missing NuGet lock dependency target graphs")
        frameworks = [e.text for e in xml.iter("TargetFramework") if e.text]
        frameworks += [f.strip() for e in xml.iter("TargetFrameworks") if e.text
                       for f in e.text.split(";") if f.strip()]
        for framework in frameworks:
            if not any(k == framework or k.startswith(framework + "/") for k in targets):
                raise ValueError(f"{rel}: missing lock target for {framework}")
        declared = {
            (e.get("Include") or e.get("Update")).lower()
            for e in xml.iter("PackageReference")
            if e.get("Include") or e.get("Update")
        }
        direct = set()
        for framework, graph in sorted(targets.items()):
            if not isinstance(graph, dict):
                raise ValueError(f"{rel}: NuGet lock target {framework} invalid")
            for name, package in sorted(graph.items()):
                if not isinstance(package, dict):
                    raise ValueError(f"{rel}: invalid NuGet dependency {name}")
                kind = package.get("type")
                if kind == "Project":
                    continue
                if kind not in ("Direct", "Transitive", "CentralTransitive"):
                    raise ValueError(f"{rel}: unsupported NuGet dependency type for {name}: {kind}")
                version = package.get("resolved")
                if not isinstance(version, str) or not version:
                    raise ValueError(f"{rel}: missing resolved version for {name}")
                digest = sha512_hex(package.get("contentHash"), f"{rel}:{name}")
                if kind == "Direct":
                    direct.add(name.lower())
                normalized = name.lower()
                purl = f"pkg:nuget/{quote(normalized, safe='')}@{quote(version, safe='')}"
                add_component(components, owners, purl, normalized, version,
                              digest, rel + "|" + framework)
                entry_count += 1
        for missing in sorted(declared - direct):
            raise ValueError(f"{rel}: direct PackageReference not locked: {missing}")
    if not components:
        raise ValueError("NuGet: no locked package dependencies in repository")
    return bom_for("Omnia .NET workspace dependency locks", None,
                   components, owners, "omnia:nuget:projects",
                   str(len(projects)), entry_count)


def build_boms(root: Path):
    return npm_bom(root), nuget_bom(root)


def serialized(bom: dict):
    return (json.dumps(bom, ensure_ascii=False, sort_keys=True,
                       indent=2, separators=(",", ": ")) + "\n").encode("utf-8")


def main():
    args = argparse.ArgumentParser(description=__doc__)
    choice = args.add_mutually_exclusive_group(required=True)
    choice.add_argument("--output", type=Path)
    choice.add_argument("--verify", type=Path)
    options = args.parse_args()
    root = Path(__file__).resolve().parents[2]
    try:
        boms = build_boms(root)
        directory = options.output or options.verify
        expected = {
            "omnia-npm.cdx.json": serialized(boms[0]),
            "omnia-nuget.cdx.json": serialized(boms[1])
        }
        if options.output is not None:
            directory.mkdir(parents=True, exist_ok=True)
        for filename, data in expected.items():
            path = directory / filename
            if options.verify is not None:
                if not path.exists() or path.read_bytes() != data:
                    raise ValueError(f"SBOM drift, missing file or lock mismatch: {path}")
            else:
                path.write_bytes(data)
            print(f"{path}: {len(data)} bytes, "
                  f"{len(boms[0 if 'npm' in filename else 1]['components'])} packages")
        print("Deterministic CycloneDX 1.6 locked dependency inventory PASS"
              + (" (verified)" if options.verify is not None else " (generated)"))
        return 0
    except (ValueError, OSError, TypeError, KeyError) as exc:
        print(f"Locked SBOM generation FAILED: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
