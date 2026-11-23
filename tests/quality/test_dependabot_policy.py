"""Dependabot TDD: reject unreviewed upgrades and missing monorepo graphs."""
import copy
import json
import tempfile
import unittest
from pathlib import Path

from verify_dependabot_policy import validate_policy, verify


class DependabotPolicyTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        for path in ("src/mobile/package.json", "src/backend/Omnia.Api/Omnia.Api.csproj",
                     "tests/Omnia.Tests/Omnia.Tests.csproj", ".github/workflows/ci.yml",
                     "docker/Dockerfile.api"):
            dest = self.root / path
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_text("", encoding="utf-8")
        self.config = {
            "version": 2,
            "updates": [
                {"package-ecosystem": ecosystem, **location,
                 "schedule": {"interval": "weekly"},
                 "open-pull-requests-limit": 5, "labels": ["area:quality"],
                 **({"groups": {"nuget-across-projects": {"group-by": "dependency-name"}}}
                    if ecosystem == "nuget" else {})}
                for ecosystem, location in (
                    ("npm", {"directory": "/src/mobile"}),
                    ("nuget", {"directories": ["/src/backend/*", "/tests/*"]}),
                    ("github-actions", {"directory": "/"}),
                    ("docker", {"directory": "/docker"}))
            ]
        }

    def test_reviewed_config_watches_all_current_graphs(self):
        validate_policy(self.config, self.root)

    def test_actual_repository_config_is_valid(self):
        verify(Path(__file__).resolve().parents[2])

    def test_missing_nuget_ecosystem_or_wrong_directories_fails_closed(self):
        changed = copy.deepcopy(self.config)
        changed["updates"] = [x for x in changed["updates"]
                              if x["package-ecosystem"] != "nuget"]
        with self.assertRaisesRegex(ValueError, "four"):
            validate_policy(changed, self.root)
        changed = copy.deepcopy(self.config)
        changed["updates"][1]["directories"] = ["/src/backend/*"]
        with self.assertRaisesRegex(ValueError, "coverage"):
            validate_policy(changed, self.root)

    def test_new_nested_nuget_project_requires_policy_update(self):
        project = self.root / "src/backend/nested/Other/Other.csproj"
        project.parent.mkdir(parents=True)
        project.touch()
        with self.assertRaisesRegex(ValueError, "outside watched"):
            validate_policy(self.config, self.root)
        project.unlink()
        project = self.root / "tests/nested/Other/Other.csproj"
        project.parent.mkdir(parents=True)
        project.touch()
        with self.assertRaisesRegex(ValueError, "outside watched"):
            validate_policy(self.config, self.root)

    def test_no_silent_ignored_versions_other_branch_or_unsafe_auto_merge(self):
        for key, val in (("ignore", [{"dependency-name": "*"}]),
                         ("target-branch", "develop"), ("auto-merge", True)):
            with self.subTest(key=key):
                changed = copy.deepcopy(self.config)
                changed["updates"][0][key] = val
                with self.assertRaisesRegex(ValueError, "unsafe"):
                    validate_policy(changed, self.root)

    def test_no_disabled_schedule_unbounded_prs_or_unreviewed_grouping(self):
        for field, value in (("schedule", {"interval": "daily"}),
                             ("open-pull-requests-limit", 0),
                             ("open-pull-requests-limit", 100),
                             ("groups", {"everything": {"patterns": ["*"]}})):
            with self.subTest(field=field, value=value):
                changed = copy.deepcopy(self.config)
                changed["updates"][0][field] = value
                with self.assertRaises(ValueError):
                    validate_policy(changed, self.root)

    def test_yaml_json_subset_rejects_invalid_and_missing_actual_file(self):
        with self.assertRaisesRegex(ValueError, "missing or invalid"):
            verify(self.root)
        path = self.root / ".github/dependabot.yml"
        path.write_text("version: 2\nupdates:\n  - package-ecosystem: npm\n")
        with self.assertRaisesRegex(ValueError, "missing or invalid"):
            verify(self.root)
        path.write_text(json.dumps(self.config))
        verify(self.root)


if __name__ == "__main__":
    unittest.main()
