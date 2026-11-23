"""TDD tests for Microsoft multiarch manifest INDEX pins (not platform digests)."""
import tempfile
import unittest
from pathlib import Path

from verify_dotnet_image_pins import extract_pins, verify_index

HASH = "sha256:" + "a" * 64
CHILD_AMD = "sha256:" + "b" * 64
CHILD_ARM = "sha256:" + "c" * 64
TAGS = (
    "mcr.microsoft.com/dotnet/sdk:10.0.404-noble",
    "mcr.microsoft.com/dotnet/aspnet:10.0.15-noble",
)


def index():
    return {
        "mediaType": "application/vnd.oci.image.index.v1+json",
        "digest": HASH,
        "manifests": [
            {"mediaType": "application/vnd.oci.image.manifest.v1+json",
             "digest": CHILD_AMD, "platform": {"os": "linux", "architecture": "amd64"}},
            {"mediaType": "application/vnd.oci.image.manifest.v1+json",
             "digest": CHILD_ARM, "platform": {"os": "linux", "architecture": "arm64"}},
            {"mediaType": "application/vnd.oci.image.manifest.v1+json",
             "digest": "sha256:" + "d" * 64,
             "platform": {"os": "unknown", "architecture": "unknown"}}
        ]
    }


class DotnetImagePinTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        (self.root / "docker").mkdir()
        self.path = self.root / "docker/Dockerfile.api"

    def write(self, images):
        self.path.write_text("FROM " + images[0] + " AS build\nFROM " + images[1] + "\n")

    def test_only_exact_official_index_digest_refs_are_accepted(self):
        self.write([tag + "@" + HASH for tag in TAGS])
        pins, errors = extract_pins(self.root)
        self.assertEqual(errors, [])
        self.assertEqual(len(pins), 2)
        self.assertEqual(verify_index(index(), HASH), [])

    def test_floating_tags_missing_digest_or_wrong_registry_fail_closed(self):
        self.write(list(TAGS))
        self.assertIn("pin", " ".join(extract_pins(self.root)[1]))
        self.write(["example.invalid/dotnet/sdk:10.0.404-noble@" + HASH,
                    TAGS[1] + "@" + HASH])
        self.assertIn("must pin", " ".join(extract_pins(self.root)[1]))
        self.write([TAGS[0] + "@" + "sha256:1234", TAGS[1] + "@" + HASH])
        self.assertIn("SHA-256", " ".join(extract_pins(self.root)[1]))

    def test_rejects_single_arch_image_instead_of_manifest_index(self):
        single = {"mediaType": "application/vnd.oci.image.manifest.v1+json",
                  "digest": HASH, "manifests": []}
        self.assertIn("index required", " ".join(verify_index(single, HASH)))
        partial = index()
        partial["manifests"] = partial["manifests"][:1]
        self.assertIn("linux/arm64", " ".join(verify_index(partial, HASH)))

    def test_rejects_drifted_index_digest_or_invalid_child_descriptor(self):
        self.assertIn("differs", " ".join(verify_index(index(), "sha256:" + "e" * 64)))
        invalid = index()
        invalid["manifests"][1]["digest"] = "sha256:bad"
        self.assertIn("linux/arm64", " ".join(verify_index(invalid, HASH)))


if __name__ == "__main__":
    unittest.main()
