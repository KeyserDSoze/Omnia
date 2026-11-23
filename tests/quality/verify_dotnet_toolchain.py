#!/usr/bin/env python3
"""Offline policy for the exact .NET toolchain in CI, OIDC and API Docker.

This is a source contract. The real dotnet --version and Docker build in
GitHub Actions are independent execution gates.
"""
import json
import re
import sys
from pathlib import Path

SDK = "10.0.404"
ASPNET_RUNTIME = "10.0.15"
SDK_IMAGE = f"mcr.microsoft.com/dotnet/sdk:{SDK}-noble"
RUNTIME_IMAGE = f"mcr.microsoft.com/dotnet/aspnet:{ASPNET_RUNTIME}-noble"
WORKFLOWS = (".github/workflows/ci.yml", ".github/workflows/oidc-e2e.yml")


def check_toolchain(root: Path) -> list[str]:
    issues: list[str] = []
    sdk_file = root / "global.json"
    if not sdk_file.is_file():
        issues.append("global.json: exact .NET SDK pin missing")
    else:
        try:
            sdk = json.loads(sdk_file.read_text(encoding="utf-8"))["sdk"]
            if (sdk.get("version") != SDK or sdk.get("rollForward") != "disable"
                    or sdk.get("allowPrerelease") is not False):
                issues.append("global.json: require SDK " + SDK
                              + ", rollForward disable, allowPrerelease false")
        except (OSError, ValueError, TypeError, KeyError) as exc:
            issues.append(f"global.json: invalid SDK document ({exc})")

    for rel in WORKFLOWS:
        try:
            yaml = (root / rel).read_text(encoding="utf-8")
        except OSError:
            issues.append(rel + ": workflow missing")
            continue
        if not re.search(r"(?m)^\s*- uses: actions/setup-dotnet@v\d+\s*$", yaml):
            issues.append(rel + ": setup-dotnet required")
        if not re.search(r"(?m)^\s*global-json-file:\s*global\.json\s*$", yaml):
            issues.append(rel + ": must install SDK from global.json")
        if re.search(r"(?m)^\s*dotnet-version:\s*", yaml):
            issues.append(rel + ": explicit or floating SDK version instead of global.json")
        if (f'test "$(dotnet --version)" = "{SDK}"' not in yaml
                and f"test \"$(dotnet --version)\" = \"{SDK}\"" not in yaml):
            issues.append(rel + ": missing executed dotnet SDK version assertion")

    docker = root / "docker/Dockerfile.api"
    try:
        contents = docker.read_text(encoding="utf-8")
    except OSError:
        return issues + ["docker/Dockerfile.api: Dockerfile missing"]
    images = re.findall(r"(?m)^FROM\s+(\S+)", contents)
    if [image.split("@", 1)[0] for image in images] != [SDK_IMAGE, RUNTIME_IMAGE]:
        issues.append("docker/Dockerfile.api: SDK/runtime images must be exact "
                      + SDK_IMAGE + " and " + RUNTIME_IMAGE)
    if not re.search(r"(?m)^COPY\s+global\.json\s+\./\s*$", contents):
        issues.append("docker/Dockerfile.api: global.json must be in build context")
    if f'test "$(dotnet --version)" = "{SDK}"' not in contents:
        issues.append("docker/Dockerfile.api: SDK version must be verified by build stage")
    return issues


def main():
    root = Path(__file__).resolve().parents[2]
    failures = check_toolchain(root)
    if failures:
        print("Exact .NET toolchain policy FAILED:", file=sys.stderr)
        for message in failures:
            print(" - " + message, file=sys.stderr)
        return 1
    print("Exact .NET SDK " + SDK + " and ASP.NET runtime " + ASPNET_RUNTIME
          + " source policy PASS; execute real dotnet/Docker gates separately.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
