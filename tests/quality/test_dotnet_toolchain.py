"""TDD: SDK drift, prerelease, floating image tags and missing checks fail closed."""
import json
import tempfile
import unittest
from pathlib import Path

from verify_dotnet_toolchain import check_toolchain

SDK = "10.0.404"
RUNTIME = "10.0.15"


class DotnetToolchainPolicyTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.root = Path(self.dir.name)
        for name in [".github/workflows", "docker"]:
            (self.root / name).mkdir(parents=True, exist_ok=True)
        self.good()

    def good(self):
        (self.root / "global.json").write_text(json.dumps({
            "sdk": {"version": SDK, "rollForward": "disable",
                    "allowPrerelease": False}
        }), encoding="utf-8")
        for workflow in ["ci.yml", "oidc-e2e.yml"]:
            (self.root / ".github/workflows" / workflow).write_text(
                "jobs:\n  build:\n    steps:\n"
                "      - uses: actions/setup-dotnet@v4\n"
                "        with:\n          global-json-file: global.json\n"
                f'      - run: test "$(dotnet --version)" = "{SDK}"\n',
                encoding="utf-8"
            )
        (self.root / "docker/Dockerfile.api").write_text(
            f"FROM mcr.microsoft.com/dotnet/sdk:{SDK}-noble AS build\n"
            "COPY global.json ./\n"
            f'RUN test "$(dotnet --version)" = "{SDK}"\n'
            f"FROM mcr.microsoft.com/dotnet/aspnet:{RUNTIME}-noble\n",
            encoding="utf-8"
        )

    def test_accepts_exact_toolchain_with_verified_builder_and_workflows(self):
        self.assertEqual(check_toolchain(self.root), [])

    def test_fails_missing_sdk_and_any_patch_or_prerelease_rollforward(self):
        (self.root / "global.json").unlink()
        self.assertIn("pin missing", "\n".join(check_toolchain(self.root)))
        self.good()
        sdk = self.root / "global.json"
        data = json.loads(sdk.read_text())
        data["sdk"]["rollForward"] = "latestPatch"
        sdk.write_text(json.dumps(data))
        self.assertIn("rollForward", "\n".join(check_toolchain(self.root)))
        data["sdk"]["rollForward"] = "disable"
        data["sdk"]["allowPrerelease"] = True
        sdk.write_text(json.dumps(data))
        self.assertIn("allowPrerelease", "\n".join(check_toolchain(self.root)))

    def test_fails_floating_ci_version_or_missing_runtime_assertion(self):
        ci = self.root / ".github/workflows/ci.yml"
        ci.write_text(ci.read_text().replace(
            "global-json-file: global.json", "dotnet-version: '10.0.x'"))
        msg = "\n".join(check_toolchain(self.root))
        self.assertIn("install SDK from global.json", msg)
        self.assertIn("floating SDK", msg)
        self.good()
        ci.write_text(ci.read_text().replace(
            f'test "$(dotnet --version)" = "{SDK}"', "dotnet --version"))
        self.assertIn("version assertion", "\n".join(check_toolchain(self.root)))

    def test_fails_floating_docker_images_or_missing_global_json_copy(self):
        file = self.root / "docker/Dockerfile.api"
        file.write_text(file.read_text().replace(
            f"sdk:{SDK}-noble", "sdk:10.0").replace(
            f"aspnet:{RUNTIME}-noble", "aspnet:10.0"))
        self.assertIn("images must be exact", "\n".join(check_toolchain(self.root)))
        self.good()
        file.write_text(file.read_text().replace("COPY global.json ./\n", ""))
        self.assertIn("build context", "\n".join(check_toolchain(self.root)))


if __name__ == "__main__":
    unittest.main()
