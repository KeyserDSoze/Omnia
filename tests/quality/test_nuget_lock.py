"""TDD contract: all projects, all direct NuGet refs and SHA-512 hashes must be locked."""
import base64
import json
import tempfile
import unittest
from pathlib import Path

from verify_nuget_locks import check_repository, check_lock_graph

HASH = base64.b64encode(b"Z" * 64).decode("ascii")


class NugetLockPolicyTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.api = self.root / "src/backend/Api/Api.csproj"
        self.api.parent.mkdir(parents=True)
        self.api.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>'
            '<PackageReference Include="Npgsql" Version="10.0.3" />'
            '</ItemGroup></Project>', encoding="utf-8"
        )
        self.lock = self.api.with_name("packages.lock.json")

    def valid_lock(self):
        return {
            "version": 1,
            "dependencies": {
                "net10.0": {
                    "Npgsql": {
                        "type": "Direct", "requested": "[10.0.3, )",
                        "resolved": "10.0.3", "contentHash": HASH
                    },
                    "System.Memory": {
                        "type": "Transitive", "resolved": "4.5.5",
                        "contentHash": HASH
                    }
                }
            }
        }

    def save(self, contents):
        self.lock.write_text(json.dumps(contents), encoding="utf-8")

    def test_all_projects_require_real_lockfiles(self):
        self.assertIn("lockfile missing", "\n".join(check_repository(self.root)))
        self.save(self.valid_lock())
        self.assertEqual(check_repository(self.root), [])
        second = self.root / "tests/A/A.csproj"
        second.parent.mkdir(parents=True)
        second.write_text('<Project><PropertyGroup><TargetFramework>net10.0'
                          '</TargetFramework></PropertyGroup></Project>', encoding="utf-8")
        self.assertIn("lockfile missing", "\n".join(check_repository(self.root)))

    def test_rejects_unlocked_direct_references_and_target_mismatch(self):
        valid = self.valid_lock()
        del valid["dependencies"]["net10.0"]["Npgsql"]
        self.save(valid)
        self.assertIn("npgsql is not locked", "\n".join(check_lock_graph(self.api)))
        valid = self.valid_lock()
        valid["dependencies"]["net9.0"] = valid["dependencies"].pop("net10.0")
        self.save(valid)
        self.assertIn("target graph missing", "\n".join(check_lock_graph(self.api)))

    def test_rejects_corrupt_or_missing_hashes_and_resolved_versions(self):
        valid = self.valid_lock()
        valid["dependencies"]["net10.0"]["Npgsql"]["contentHash"] = "invalid"
        self.save(valid)
        self.assertIn("SHA-512", "\n".join(check_lock_graph(self.api)))
        valid = self.valid_lock()
        del valid["dependencies"]["net10.0"]["System.Memory"]["resolved"]
        self.save(valid)
        self.assertIn("no resolved version", "\n".join(check_lock_graph(self.api)))

    def test_project_references_do_not_require_package_hashes(self):
        valid = self.valid_lock()
        valid["dependencies"]["net10.0"]["Omnia.Domain"] = {
            "type": "Project", "dependencies": {}
        }
        self.save(valid)
        self.assertEqual(check_lock_graph(self.api), [])


if __name__ == "__main__":
    unittest.main()
