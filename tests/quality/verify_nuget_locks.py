#!/usr/bin/env python3
"""Fail-closed, offline structural policy for all Omnia NuGet lock graphs.

NuGet itself is the authority for package compatibility: always follow
this preflight with dotnet restore --locked-mode from a clean checkout.
"""
import base64
import binascii
import json
import sys
from pathlib import Path
from xml.etree import ElementTree

PROJECT_TREES = ("src/backend", "tests")


def project_paths(root: Path):
    paths = []
    for relative in PROJECT_TREES:
        directory = root / relative
        paths.extend(p for p in directory.rglob("*.csproj")
                     if "obj" not in p.parts and "bin" not in p.parts)
    return sorted(paths)


def check_lock_graph(project: Path):
    errors = []
    lock_path = project.with_name("packages.lock.json")
    if not lock_path.is_file():
        return [f"{lock_path}: lockfile missing"]
    try:
        xml = ElementTree.parse(project).getroot()
        lock = json.loads(lock_path.read_text(encoding="utf-8"))
    except (ElementTree.ParseError, OSError, UnicodeDecodeError,
            ValueError) as exc:
        return [f"{lock_path}: cannot parse project or lock ({exc})"]
    if not isinstance(lock, dict) or lock.get("version") not in (1, 2, 3):
        return [f"{lock_path}: unsupported or invalid NuGet lockfile version"]
    targets = lock.get("dependencies")
    if not isinstance(targets, dict) or not targets:
        return [f"{lock_path}: dependency target graphs missing"]
    frameworks = [e.text for e in xml.iter("TargetFramework") if e.text]
    frameworks += [s.strip() for e in xml.iter("TargetFrameworks")
                   if e.text for s in e.text.split(";") if s.strip()]
    for framework in frameworks:
        if not any(k == framework or k.startswith(framework + "/") for k in targets):
            errors.append(f"{lock_path}: target graph missing for {framework}")
    references = set()
    for element in xml.iter("PackageReference"):
        name = element.get("Include") or element.get("Update")
        if name:
            references.add(name.lower())
    found_direct = set()
    for framework, graph in targets.items():
        if not isinstance(graph, dict):
            errors.append(f"{lock_path}: target {framework} is not an object")
            continue
        for name, package in graph.items():
            if not isinstance(package, dict):
                errors.append(f"{lock_path}: invalid package {name}")
                continue
            kind = package.get("type")
            if kind == "Direct":
                found_direct.add(name.lower())
            if kind == "Project":
                continue
            if not isinstance(package.get("resolved"), str) or not package["resolved"]:
                errors.append(f"{lock_path}: package {name} has no resolved version")
            checksum = package.get("contentHash")
            try:
                decoded = base64.b64decode(checksum, validate=True) if isinstance(checksum, str) else b""
                if len(decoded) != 64 or base64.b64encode(decoded).decode() != checksum:
                    raise ValueError("not 64-byte SHA-512")
            except (ValueError, binascii.Error):
                errors.append(f"{lock_path}: package {name} lacks valid SHA-512 contentHash")
    for name in sorted(references - found_direct):
        errors.append(f"{lock_path}: direct PackageReference {name} is not locked")
    return errors


def check_repository(root: Path):
    projects = project_paths(root)
    if not projects:
        return ["No .NET csproj projects found"]
    errors = []
    for project in projects:
        errors.extend(check_lock_graph(project))
    return errors


def main():
    repository_root = Path(__file__).resolve().parents[2]
    issues = check_repository(repository_root)
    if issues:
        print("NuGet lock validation FAILED:", file=sys.stderr)
        for issue in issues:
            print(" - " + issue, file=sys.stderr)
        return 1
    print(f"All {len(project_paths(repository_root))} .NET project NuGet graphs passed structural SHA-512 checks.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
