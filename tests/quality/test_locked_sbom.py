"""Test-first requirements for reproducible, lock-derived CycloneDX inventories."""
import base64
import copy
import json
import tempfile
import unittest
from pathlib import Path

from generate_locked_sbom import build_boms, serialized

HASH_A = base64.b64encode(b"A" * 64).decode("ascii")
HASH_B = base64.b64encode(b"B" * 64).decode("ascii")


class SbomPolicyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "src/mobile").mkdir(parents=True)
        (self.root / "tests/Omnia.Test").mkdir(parents=True)
        self.package_manifest = {
            "name": "@omnia/mobile", "version": "0.1.0",
            "dependencies": {"@org/main": "1.0.0"}, "devDependencies": {}
        }
        self.npm = {
            "name": "@omnia/mobile", "version": "0.1.0", "lockfileVersion": 3,
            "packages": {
                "": {"name": "@omnia/mobile", "version": "0.1.0",
                     "dependencies": {"@org/main": "1.0.0"}, "devDependencies": {}},
                "node_modules/@org/main": {
                    "version": "1.0.0",
                    "resolved": "https://registry.npmjs.org/@org/main/-/main-1.0.0.tgz",
                    "integrity": "sha512-" + HASH_A
                },
                "node_modules/@org/main/node_modules/nested": {
                    "version": "2.0.0",
                    "resolved": "https://registry.npmjs.org/nested/-/nested-2.0.0.tgz",
                    "integrity": "sha512-" + HASH_B
                }
            }
        }
        self.nuget = {
            "version": 1,
            "dependencies": {
                "net10.0": {
                    "Npgsql": {"type": "Direct", "resolved": "10.0.0",
                               "contentHash": HASH_A},
                    "Omnia.Domain": {"type": "Project"},
                    "System.Memory": {"type": "Transitive", "resolved": "4.5.5",
                                      "contentHash": HASH_B}
                }
            }
        }
        self.write()

    def write(self):
        (self.root / "src/mobile/package.json").write_text(
            json.dumps(self.package_manifest), encoding="utf-8")
        (self.root / "src/mobile/package-lock.json").write_text(
            json.dumps(self.npm), encoding="utf-8")
        project = self.root / "tests/Omnia.Test/Omnia.Test.csproj"
        project.write_text('<Project Sdk="Microsoft.NET.Sdk">'
                           '<PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                           '</PropertyGroup><ItemGroup>'
                           '<PackageReference Include="Npgsql" Version="10.0.0" />'
                           '</ItemGroup></Project>', encoding="utf-8")
        project.with_name("packages.lock.json").write_text(
            json.dumps(self.nuget), encoding="utf-8")

    def test_scoped_nested_npm_and_nuget_transitives_have_purls_and_sha512(self):
        npm, nuget = build_boms(self.root)
        self.assertEqual(npm["bomFormat"], "CycloneDX")
        self.assertEqual(npm["specVersion"], "1.6")
        self.assertEqual(len(npm["components"]), 2)
        self.assertEqual([c["purl"] for c in npm["components"]],
                         ["pkg:npm/%40org/main@1.0.0", "pkg:npm/nested@2.0.0"])
        self.assertEqual(npm["components"][0]["hashes"][0],
                         {"alg": "SHA-512", "content": "41" * 64})
        self.assertEqual(len(nuget["components"]), 2)
        self.assertEqual({x["purl"] for x in nuget["components"]},
                         {"pkg:nuget/npgsql@10.0.0",
                          "pkg:nuget/system.memory@4.5.5"})
        self.assertTrue(all("bom-ref" in x for x in npm["components"] +
                            nuget["components"]))
        self.assertIn("Omnia.Test.csproj", str(nuget["components"]))

    def test_repeat_generation_is_byte_identical_and_source_order_independent(self):
        a = tuple(serialized(x) for x in build_boms(self.root))
        self.npm["packages"] = dict(reversed(list(self.npm["packages"].items())))
        self.nuget["dependencies"]["net10.0"] = dict(
            reversed(list(self.nuget["dependencies"]["net10.0"].items())))
        self.write()
        b = tuple(serialized(x) for x in build_boms(self.root))
        self.assertEqual(a, b)

    def test_missing_project_lock_or_unlisted_root_dependency_fails_closed(self):
        (self.root / "tests/Omnia.Test/packages.lock.json").unlink()
        with self.assertRaisesRegex(ValueError, "packages.lock.json"):
            build_boms(self.root)
        self.write()
        del self.npm["packages"]["node_modules/@org/main"]
        self.write()
        with self.assertRaisesRegex(ValueError, "direct dependency"):
            build_boms(self.root)

    def test_corrupt_hash_and_conflicting_nuget_package_identity_are_rejected(self):
        self.npm["packages"]["node_modules/@org/main"]["integrity"] = "sha512-bad"
        self.write()
        with self.assertRaisesRegex(ValueError, "SHA-512"):
            build_boms(self.root)
        self.npm["packages"]["node_modules/@org/main"]["integrity"] = "sha512-" + HASH_A
        other = self.root / "src/backend/Other"
        other.mkdir(parents=True)
        (other / "Other.csproj").write_text(
            '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
            '</PropertyGroup></Project>')
        conflict = copy.deepcopy(self.nuget)
        conflict["dependencies"]["net10.0"]["Npgsql"]["contentHash"] = HASH_B
        (other / "packages.lock.json").write_text(json.dumps(conflict))
        self.write()
        with self.assertRaisesRegex(ValueError, "conflicting"):
            build_boms(self.root)

    def test_nuget_unlocked_direct_or_missing_target_and_npm_unsafe_url_fail(self):
        del self.nuget["dependencies"]["net10.0"]["Npgsql"]
        self.write()
        with self.assertRaisesRegex(ValueError, "direct PackageReference"):
            build_boms(self.root)
        self.nuget["dependencies"]["net10.0"]["Npgsql"] = {
            "type": "Direct", "resolved": "10.0.0", "contentHash": HASH_A
        }
        self.nuget["dependencies"]["net9.0"] = self.nuget["dependencies"].pop("net10.0")
        self.write()
        with self.assertRaisesRegex(ValueError, "net10.0"):
            build_boms(self.root)
        self.nuget["dependencies"]["net10.0"] = self.nuget["dependencies"].pop("net9.0")
        self.npm["packages"]["node_modules/@org/main"]["resolved"] = "https://evil.invalid/x.tgz"
        self.write()
        with self.assertRaisesRegex(ValueError, "registry"):
            build_boms(self.root)


if __name__ == "__main__":
    unittest.main()
