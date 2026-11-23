#!/usr/bin/env python3
"""Read-only, fail-closed npm/NuGet advisory scans with audit evidence.

The runner never installs/updates dependencies or modifies package locks.
The resulting JSON is a *security finding report*, not a clean release
attestation. Registry/advisory outages are failures, not zero findings.
"""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys

from generate_locked_sbom import build_boms, project_paths
from supply_chain_audit import (
    assess_findings, parse_npm, parse_nuget, validate_scanner_result
)


def read_json(path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, UnicodeDecodeError) as exc:
        raise ValueError(f"invalid/missing JSON file {path}: {exc}") from exc


def run_command(command, root, timeout, ecosystem):
    try:
        result = subprocess.run(command, cwd=root, capture_output=True,
                                text=True, timeout=timeout, check=False,
                                env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise ValueError(f"{ecosystem} advisory scanner unavailable or timed out: {exc}") from exc
    # No untrusted raw stderr in the public output report: diagnostics can
    # accidentally include proxy settings, HTTP headers and credentials.
    if ecosystem == "nuget" and result.returncode != 0:
        raise ValueError(f"NuGet scanner/locked restore failed (exit {result.returncode})")
    if "NU1900" in result.stderr or "NU1905" in result.stderr:
        raise ValueError("NuGet vulnerability advisory feed unavailable (NU1900/NU1905)")
    try:
        report = json.loads(result.stdout)
    except (ValueError, UnicodeDecodeError) as exc:
        raise ValueError(f"{ecosystem} scanner returned non-JSON or partial output") from exc
    validate_scanner_result(result.returncode, report, ecosystem)
    return report


def npm_index(root):
    npm = read_json(root / "src/mobile/package-lock.json")
    if npm.get("lockfileVersion") != 3 or not isinstance(npm.get("packages"), dict):
        raise ValueError("npm v3 lockfile missing")
    locked = {}
    for path, package in npm["packages"].items():
        if not path:
            continue
        if not isinstance(package, dict) or "node_modules/" not in path:
            raise ValueError("unexpected npm package path")
        name = package.get("name") or path.rsplit("node_modules/", 1)[-1]
        version = package.get("version")
        if not isinstance(version, str):
            raise ValueError("npm package missing locked version")
        locked.setdefault(name.lower(), set()).add(version)
    if not locked:
        raise ValueError("npm locked package graph empty")
    return locked


def nuget_index(project):
    lock = read_json(project.with_name("packages.lock.json"))
    graphs = lock.get("dependencies")
    if not isinstance(graphs, dict) or not graphs:
        raise ValueError("NuGet graph missing for project " + str(project))
    indexed = {}
    for graph in graphs.values():
        if not isinstance(graph, dict):
            raise ValueError("NuGet graph invalid")
        for name, package in graph.items():
            if not isinstance(package, dict):
                raise ValueError("NuGet package object invalid")
            if package.get("type") == "Project":
                continue
            version = package.get("resolved")
            if not isinstance(version, str):
                raise ValueError("NuGet package version not locked")
            indexed.setdefault(name.lower(), set()).add(version)
    return indexed


def run_audit(root, exceptions, commit_sha):
    # Independent source contract, before registry calls; supplies entire
    # npm+NuGet lock inventory and checks every one of the 20 graphs.
    npm_bom, nuget_bom = build_boms(root)
    projects = project_paths(root)
    if len(projects) != 20:
        raise ValueError(f"expected exactly 20 .NET project lock graphs; got {len(projects)}")
    findings = []
    scopes = {}
    known_npm = npm_index(root)
    for scope, extra in (("all", []), ("production", ["--omit=dev"])):
        report = run_command([
            "npm", "audit", "--json", "--audit-level=high",
            "--registry=https://registry.npmjs.org", *extra
        ], root / "src/mobile", 180, "npm")
        subset = parse_npm(report, known_npm)
        for item in subset:
            findings.append({**item, "scope": scope})
        scopes[scope] = {
            "totalPackagesFlagged": report["metadata"]["vulnerabilities"]["total"],
            "high": report["metadata"]["vulnerabilities"]["high"],
            "critical": report["metadata"]["vulnerabilities"]["critical"],
            "moderate": report["metadata"]["vulnerabilities"]["moderate"],
            "low": report["metadata"]["vulnerabilities"]["low"]
        }
    total_nuget = 0
    for project in projects:
        relative = project.relative_to(root).as_posix()
        # Force fresh audit on clean runner and locked package versions:
        # - NuGetAudit/Mode explicitly selects transitive package audit
        # - --locked-mode rejects drift instead of silently regenerating
        # - NuGet Audit source failure is never treated as a successful scan
        try:
            result = subprocess.run(
                ["dotnet", "restore", relative, "--locked-mode",
                 "-p:NuGetAudit=true", "-p:NuGetAuditMode=all",
                 "-p:NuGetAuditLevel=low"],
                cwd=root, check=False, capture_output=True, text=True,
                timeout=120)
        except (OSError, subprocess.TimeoutExpired) as exc:
            raise ValueError(f"NuGet locked audited restore unavailable: {relative}") from exc
        if result.returncode != 0 or any(
                marker in result.stdout + result.stderr
                for marker in ("NU1900", "NU1905")):
            raise ValueError(f"NuGet audited restore blocked by feed/warnings: {relative}")
        report = run_command([
            "dotnet", "package", "list", "--project", relative,
            "--vulnerable", "--include-transitive", "--format", "json",
            "--output-version", "1", "--no-restore"
        ], root, 120, "nuget")
        subset = parse_nuget(report, relative, nuget_index(project))
        findings.extend(subset)
        total_nuget += 1
        print(f"NuGet transitive advisory source checked: {relative} ({len(subset)} findings)",
              flush=True)

    evaluation = assess_findings(findings, exceptions, datetime.now(timezone.utc).date())
    summary = {
        "version": 1,
        "sourceCommit": commit_sha,
        "status": "blocked" if evaluation["blocked"] else "pass",
        "scanned": {
            "npmPackagesUnique": len(npm_bom["components"]),
            "nugetPackagesUnique": len(nuget_bom["components"]),
            "nugetProjectReports": total_nuget,
            "npmScopes": scopes
        },
        "counts": {k: len(evaluation[k]) for k in ("blocked", "accepted", "warnings")},
        "findings": evaluation
    }
    return summary


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--output", type=Path, required=True)
    options = cli.parse_args()
    output = options.output
    output.parent.mkdir(parents=True, exist_ok=True)
    root = Path(__file__).resolve().parents[2]
    evidence = {"version": 1, "sourceCommit": os.environ.get("GITHUB_SHA", "local"),
                "status": "incomplete", "scanned": {}, "counts": {}, "findings": {}}
    try:
        raw = read_json(root / "security/audit-exceptions.json")
        if not isinstance(raw, dict) or set(raw) != {"version", "exceptions"} or raw["version"] != 1:
            raise ValueError("security exceptions schema invalid or missing")
        # No waiver is applied until all scanners have returned valid reports.
        evidence = run_audit(root, raw["exceptions"], evidence["sourceCommit"])
    except (ValueError, OSError, TypeError, KeyError) as exc:
        evidence["status"] = "incomplete"
        evidence["error"] = str(exc)
    finally:
        output.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n",
                          encoding="utf-8")
    if evidence["status"] != "pass":
        print("SUPPLY CHAIN AUDIT GATE FAILED: " + evidence["status"], file=sys.stderr)
        if evidence.get("error"):
            print("Scanner or report failure: " + evidence["error"], file=sys.stderr)
        for blocked in evidence.get("findings", {}).get("blocked", []):
            print("BLOCKED: {ecosystem} {package}@{version} {severity} {advisory}".format(**blocked),
                  file=sys.stderr)
        print(f"Report: {output}", file=sys.stderr)
        return 1
    print("SUPPLY CHAIN AUDIT PASS: no unsuppressed HIGH/CRITICAL advisories, "
          "all scanners and twenty project audits completed.")
    print(f"Report: {output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
