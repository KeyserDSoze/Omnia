#!/usr/bin/env python3
"""Fail-closed, dependency-free policy for Omnia's GitHub Dependabot PR proposals.

Dependabot's YAML config is authored in JSON-compatible YAML 1.2 syntax.
This lets the CI validate its actual tree using Python's standard JSON parser
instead of installing an unpinned YAML parser. This is local policy validation,
not a GitHub Dependabot service execution or evidence a PR was created.
"""
import json
import sys
from pathlib import Path

EXPECTED = {
    "npm": {"directory": "/src/mobile"},
    "nuget": {"directories": ["/src/backend/*", "/tests/*"]},
    "github-actions": {"directory": "/"},
    "docker": {"directory": "/docker"},
}
ALLOWED_UPDATE_KEYS = {
    "package-ecosystem", "directory", "directories", "schedule",
    "open-pull-requests-limit", "labels", "groups",
}


def validate_policy(config: object, root: Path) -> None:
    """Reject config changes that leave a dependency graph unwatched."""
    if not isinstance(config, dict) or set(config) != {"version", "updates"}:
        raise ValueError("Dependabot config must contain only version and updates")
    if type(config["version"]) is not int or config["version"] != 2:
        raise ValueError("Dependabot config version must be 2")
    updates = config["updates"]
    if not isinstance(updates, list) or len(updates) != len(EXPECTED):
        raise ValueError("Dependabot must cover all four package ecosystems")

    seen = set()
    for update in updates:
        if not isinstance(update, dict) or set(update) - ALLOWED_UPDATE_KEYS:
            raise ValueError("Dependabot update has unsupported or unsafe keys")
        ecosystem = update.get("package-ecosystem")
        if ecosystem not in EXPECTED or ecosystem in seen:
            raise ValueError("Dependabot has a missing, duplicate or unexpected ecosystem")
        seen.add(ecosystem)
        expected = EXPECTED[ecosystem]
        for key in ("directory", "directories"):
            if key in expected:
                if update.get(key) != expected[key]:
                    raise ValueError(f"{ecosystem}: incorrect manifest {key} coverage")
            elif key in update:
                raise ValueError(f"{ecosystem}: unexpected {key}")
        if update.get("schedule") != {"interval": "weekly"}:
            raise ValueError(f"{ecosystem}: updates must be weekly and reviewed")
        count = update.get("open-pull-requests-limit")
        if type(count) is not int or not 1 <= count <= 5:
            raise ValueError(f"{ecosystem}: unbounded or disabled version PR limit")
        if update.get("labels") != ["area:quality"]:
            raise ValueError(f"{ecosystem}: expected existing quality review label")
        groups = update.get("groups")
        if ecosystem == "nuget":
            if groups != {"nuget-across-projects": {"group-by": "dependency-name"}}:
                raise ValueError("NuGet must group version PRs by dependency across directories")
        elif groups is not None:
            raise ValueError(f"{ecosystem}: unreviewed version update grouping")

    if seen != set(EXPECTED):
        raise ValueError("Dependabot ecosystem coverage incomplete")
    if not (root / "src/mobile/package.json").is_file():
        raise ValueError("npm manifest is missing at watched directory")
    if not list((root / ".github/workflows").glob("*.yml")):
        raise ValueError("no GitHub Actions workflows in watched root")
    if not (root / "docker/Dockerfile.api").is_file():
        raise ValueError("Dockerfile.api is missing from watched Docker directory")

    # A wildcard /src/backend/* or /tests/* covers only the immediate
    # child project directories. Catch new deeply nested .csproj as drift.
    projects = sorted(p for scope in ("src/backend", "tests")
                      for p in (root / scope).rglob("*.csproj")
                      if not {"bin", "obj"}.intersection(p.relative_to(root).parts))
    if not projects:
        raise ValueError("NuGet has no projects to monitor")
    for project in projects:
        parts = project.relative_to(root).parts
        # Both glob patterns cover one direct child project directory only.
        watched = ((len(parts) == 4 and parts[:2] == ("src", "backend"))
                   or (len(parts) == 3 and parts[0] == "tests"))
        if not watched:
            raise ValueError(f"NuGet project outside watched directory: {project.relative_to(root)}")


def verify(root: Path) -> None:
    path = root / ".github/dependabot.yml"
    try:
        config = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise ValueError("Dependabot YAML (JSON subset) missing or invalid") from exc
    validate_policy(config, root)


if __name__ == "__main__":
    try:
        verify(Path(__file__).resolve().parents[2])
    except ValueError as exc:
        print(f"DEPENDABOT_POLICY_FAILED: {exc}", file=sys.stderr)
        sys.exit(1)
    print("DEPENDABOT_POLICY_OK: four watched ecosystems; PR-only review policy")
