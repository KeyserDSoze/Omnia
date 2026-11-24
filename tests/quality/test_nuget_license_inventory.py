import base64
import hashlib
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch
from types import SimpleNamespace

from nuget_license_inventory import inspect_package, build_report, signed_hash


def digest(path):
    return base64.b64encode(hashlib.sha512(path.read_bytes()).digest()).decode()


class LicenseInventoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.cache = Path(self.temp.name)
        self.path = self.cache / 'example' / '1.2.3' / 'example.1.2.3.nupkg'
        self.path.parent.mkdir(parents=True)

    def package(self, license_xml='<license type="expression">MIT</license>',
                files=(), identity='Example', version='1.2.3', xml=None):
        xml = xml if xml is not None else (
            '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
            '<metadata><id>' + identity + '</id><version>' + version + '</version>'
            + license_xml + '</metadata></package>')
        with zipfile.ZipFile(self.path, 'w') as archive:
            archive.writestr('Example.nuspec', xml)
            for name, value in files:
                archive.writestr(name, value)
        return digest(self.path)

    def read(self, expected=None):
        return inspect_package(self.path, 'example', '1.2.3', expected or digest(self.path))

    def test_expression_preserved_and_identity_normalized(self):
        self.package('<license type="expression">MIT OR Apache-2.0</license>', version='1.2.3.0')
        row = self.read()
        self.assertEqual(row['licenseKind'], 'expression')
        self.assertEqual(row['declaredLicense'], 'MIT OR Apache-2.0')
        self.assertEqual(row['triage'], 'additional-review')
        self.assertEqual(row['contentHash'], digest(self.path))

    def test_license_file_hash_and_archive_path(self):
        value = b'license notice bytes'
        self.package('<license type="file">legal/LICENSE.txt</license>', [('legal/LICENSE.txt', value)])
        row = self.read()
        self.assertEqual(row['licenseKind'], 'file')
        self.assertEqual(row['licenseFileSha256'], hashlib.sha256(value).hexdigest())
        self.assertEqual(row['declaredLicense'], 'legal/LICENSE.txt')

    def test_legacy_url_and_missing_are_explicit_review_candidates(self):
        self.package('<licenseUrl>https://example.invalid/license</licenseUrl>')
        self.assertEqual(self.read()['licenseKind'], 'legacy-url')
        self.assertEqual(self.read()['triage'], 'additional-review')
        self.package('')
        self.assertEqual(self.read()['licenseKind'], 'missing')
        self.assertIsNone(self.read()['declaredLicense'])

    def test_hash_identity_and_version_mismatch_denied(self):
        expected = self.package()
        self.package(identity='Other')
        with self.assertRaisesRegex(ValueError, 'content hash'):
            self.read(expected)
        with self.assertRaisesRegex(ValueError, 'identity'):
            self.read()
        self.package(version='2.0.0')
        with self.assertRaisesRegex(ValueError, 'identity'):
            self.read()

    def test_invalid_xml_and_duplicate_metadata_rejected(self):
        for xml in ('<bad>', '<!DOCTYPE package><package/>',
                    '<package><metadata><id>Example</id><id>Example</id><version>1.2.3</version></metadata></package>'):
            self.package(xml=xml)
            with self.subTest(xml=xml), self.assertRaises(ValueError):
                self.read()

    def test_missing_license_file_traversal_and_ambiguous_members_rejected(self):
        for license_xml, files in (
            ('<license type="file">LICENSE.txt</license>', []),
            ('<license type="file">../LICENSE.txt</license>', []),
            ('<license type="expression">MIT</license>', [('lib/../bad', b'x')]),
            ('<license type="expression">MIT</license>', [('%2e%2e/bad', b'x')]),
            ('<license type="file">LICENSE.txt</license>', [('LICENSE.txt', b'x'), ('license.txt', b'y')])):
            self.package(license_xml, files)
            with self.subTest(license_xml=license_xml, files=files), self.assertRaises(ValueError):
                self.read()

    def test_signed_archive_uses_official_hash_and_never_raw_sha512_fallback(self):
        self.package(files=[('.signature.p7s', b'not a real signature')])
        official = base64.b64encode(b'a' * 64).decode()
        with patch('nuget_license_inventory.signed_hash', return_value=official) as verify:
            self.assertEqual(self.read(official)['hashMethod'], 'nuget-verify')
            verify.assert_called_once_with(self.path)
        with patch('nuget_license_inventory.signed_hash', side_effect=ValueError('verification failed')):
            with self.assertRaisesRegex(ValueError, 'verification failed'):
                self.read()

    def test_signed_cli_rejects_failure_missing_and_duplicate_hash(self):
        h = base64.b64encode(b'a' * 64).decode()
        for code, stdout in ((1, 'Content hash: ' + h), (0, 'no hash'),
                             (0, 'Content hash: ' + h + '\nContent hash: ' + h)):
            with patch('nuget_license_inventory.subprocess.run', return_value=SimpleNamespace(returncode=code, stdout=stdout)):
                with self.assertRaises(ValueError):
                    signed_hash(self.path)
        with patch('nuget_license_inventory.subprocess.run', return_value=SimpleNamespace(returncode=0, stdout='  Content hash: ' + h + '\n')):
            self.assertEqual(signed_hash(self.path), h)

    def test_report_coverage_determinism_and_missing_archive(self):
        h = self.package()
        component = {'name': 'example', 'version': '1.2.3',
                     'hashes': [{'alg': 'SHA-512', 'content': base64.b64decode(h).hex()}],
                     'properties': [{'name': 'omnia:lock-owners', 'value': 'a.csproj|net10.0;b.csproj|net10.0'}]}
        bom = {'components': [component]}
        result = build_report(bom, self.cache, 'a' * 40, 20)
        self.assertEqual(result, build_report(bom, self.cache, 'a' * 40, 20))
        self.assertEqual(result['counts']['packages'], 1)
        self.assertEqual(result['projectCount'], 20)
        self.assertEqual(result['packages'][0]['lockOwners'], ['a.csproj|net10.0', 'b.csproj|net10.0'])
        with self.assertRaises(ValueError):
            build_report(bom, self.cache, 'a' * 40, 19)
        with self.assertRaises(ValueError):
            build_report({'components': [component, component]}, self.cache, 'a' * 40, 20)
        self.path.unlink()
        with self.assertRaises(ValueError):
            build_report(bom, self.cache, 'a' * 40, 20)


if __name__ == '__main__':
    unittest.main()
