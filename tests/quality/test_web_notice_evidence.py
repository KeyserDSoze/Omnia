import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from web_notice_evidence import build_report


class WebNoticeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.dist = self.root / 'dist'
        self.dist.mkdir()
        (self.dist / 'index.html').write_text('<html/>')
        (self.dist / 'app.js').write_text('export {};')
        self.lock = {'lockfileVersion': 3, 'packages': {'': {},
            'node_modules/example': {'version': '1.0.0', 'license': 'MIT'},
            'node_modules/optional': {'version': '2.0.0', 'license': 'ISC', 'optional': True}}}
        self.pkg = self.root / 'node_modules/example'
        self.pkg.mkdir(parents=True)
        self.manifest = {'name': 'example', 'version': '1.0.0', 'license': 'MIT'}
        self.write_manifest()
        (self.pkg / 'LICENSE.txt').write_text('fixture license')

    def write_manifest(self):
        (self.pkg / 'package.json').write_text(json.dumps(self.manifest))

    def report(self):
        return build_report(self.root, json.dumps(self.lock).encode(), 'a' * 40)

    def test_installed_and_optional_absent(self):
        r = self.report()
        self.assertEqual(r['counts']['installed'], 1)
        self.assertEqual(r['counts']['optional-absent'], 1)
        self.assertEqual(r['packages'][0]['noticeFiles'][0]['sha256'], hashlib.sha256(b'fixture license').hexdigest())
        self.assertEqual(r['packages'][1]['status'], 'optional-absent')

    def test_required_absent_fails(self):
        self.lock['packages']['node_modules/optional'].pop('optional')
        with self.assertRaises(ValueError):
            self.report()

    def test_identity_mismatch_fails(self):
        self.manifest['version'] = '9.0.0'
        self.write_manifest()
        with self.assertRaises(ValueError):
            self.report()

    def test_missing_notice_and_different_declaration_visible(self):
        (self.pkg / 'LICENSE.txt').unlink()
        self.manifest['license'] = 'MIT OR Apache-2.0'
        self.write_manifest()
        r = self.report()
        self.assertEqual(r['counts']['no-root-notice'], 1)
        self.assertEqual(r['counts']['declaration-differs'], 1)
        self.assertEqual(r['packages'][0]['installedLicense'], 'MIT OR Apache-2.0')

    def test_unsafe_path_rejected(self):
        self.lock['packages']['node_modules/..'] = {'version': '1.0.0', 'license': 'MIT'}
        with self.assertRaises(ValueError):
            self.report()

    def test_symlink_notice_and_dist_rejected(self):
        (self.pkg / 'NOTICE').symlink_to(self.dist / 'index.html')
        with self.assertRaises(ValueError):
            self.report()
        (self.pkg / 'NOTICE').unlink()
        (self.dist / 'link.js').symlink_to(self.pkg / 'LICENSE.txt')
        with self.assertRaises(ValueError):
            self.report()

    def test_deterministic_and_byte_changes_visible(self):
        before = self.report()
        self.assertEqual(before, self.report())
        (self.dist / 'app.js').write_text('changed')
        (self.pkg / 'LICENSE.txt').write_text('changed')
        after = self.report()
        self.assertNotEqual(before['webFiles'], after['webFiles'])
        self.assertNotEqual(before['packages'], after['packages'])
        self.assertEqual(before['lockSha256'], after['lockSha256'])

    def test_export_missing_index_or_js_fails(self):
        (self.dist / 'app.js').unlink()
        with self.assertRaises(ValueError):
            self.report()


if __name__ == '__main__':
    unittest.main()
