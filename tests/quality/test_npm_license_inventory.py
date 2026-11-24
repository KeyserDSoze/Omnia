"""Behavioral tests for the npm license metadata inventory (#216)."""
import json
import unittest

from npm_license_inventory import build_inventory, make_report


def lock(*entries):
    return {'lockfileVersion': 3, 'packages': {'': {'name': 'app'}, **dict(entries)}}


def pkg(license='MIT', **fields):
    return {'version': '1.2.3', 'license': license, **fields}


class LicenseInventoryTests(unittest.TestCase):
    def test_every_occurrence_and_scope_are_preserved(self):
        data = lock(('node_modules/a', pkg()),
                    ('node_modules/a/node_modules/@x/b', pkg('MPL-2.0', dev=True)),
                    ('node_modules/c', pkg(optional=True, devOptional=True)))
        report = build_inventory(data)
        self.assertEqual(report['counts']['occurrences'], 3)
        rows = {r['path']: r for r in report['packages']}
        self.assertEqual(rows['node_modules/a/node_modules/@x/b']['name'], '@x/b')
        self.assertTrue(rows['node_modules/a/node_modules/@x/b']['dev'])
        self.assertTrue(rows['node_modules/c']['optional'])
        self.assertTrue(rows['node_modules/c']['devOptional'])

    def test_compound_and_special_licenses_require_additional_review(self):
        data = lock(('node_modules/a', pkg('(BSD-3-Clause OR GPL-2.0)')),
                    ('node_modules/b', pkg('MPL-2.0')),
                    ('node_modules/c', pkg('CC-BY-4.0')))
        result = build_inventory(data)
        self.assertEqual(result['status'], 'inventory-complete')
        self.assertEqual(result['counts']['additional-review'], 3)
        self.assertEqual(result['packages'][0]['declaredLicense'], '(BSD-3-Clause OR GPL-2.0)')

    def test_unknown_or_missing_metadata_blocks_but_is_visible(self):
        data = lock(('node_modules/a', pkg('UNLICENSED')),
                    ('node_modules/b', {'version': '1.2.3'}),
                    ('node_modules/c', pkg('MIT AND AGPL-3.0-only')))
        result = build_inventory(data)
        self.assertEqual(result['status'], 'blocked')
        self.assertEqual(result['counts']['unclassified'], 3)
        self.assertIsNone(result['packages'][1]['declaredLicense'])

    def test_no_implicit_or_branch_selection_or_substring_allowlist(self):
        for expression in ('MIT OR Proprietary', 'MIT ', 'SEE LICENSE IN LICENSE', 'MIT AND GPL-3.0-only'):
            with self.subTest(expression=expression):
                self.assertEqual(build_inventory(lock(('node_modules/a', pkg(expression))))['status'], 'blocked')

    def test_order_deterministic_with_duplicate_package_occurrences(self):
        a = ('node_modules/a', pkg())
        b = ('node_modules/b/node_modules/a', pkg())
        self.assertEqual(build_inventory(lock(a, b)), build_inventory(lock(b, a)))
        self.assertEqual(build_inventory(lock(a, b))['counts']['occurrences'], 2)

    def test_invalid_lock_and_malformed_scope_rejected(self):
        for data in ({'lockfileVersion': 2, 'packages': {}}, lock(),
                     lock(('outside/a', pkg())), lock(('node_modules/a', pkg(dev='false'))),
                     lock(('node_modules/a', {'license': 'MIT'})),
                     lock(('node_modules/a', pkg({'type': 'MIT'})))):
            with self.subTest(data=data), self.assertRaises(ValueError):
                build_inventory(data)

    def test_exact_source_digest_and_sha_are_reported(self):
        import hashlib
        raw = json.dumps(lock(('node_modules/a', pkg()))).encode()
        result = make_report(raw, 'a' * 40)
        self.assertEqual(result['lockSha256'], hashlib.sha256(raw).hexdigest())
        self.assertEqual(result['sourceCommit'], 'a' * 40)
        self.assertEqual(result['packages'][0]['triage'], 'notice-review')

    def test_duplicate_json_keys_rejected(self):
        raw = b'{"lockfileVersion":3,"lockfileVersion":3,"packages":{}}'
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            make_report(raw, 'a' * 40)


if __name__ == '__main__':
    unittest.main()
