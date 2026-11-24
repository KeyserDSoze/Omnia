"""TDD: audit warnings need a fresh, exact, non-waiving human risk review."""
import copy
import unittest
from datetime import date

from verify_moderate_reviews import validate_review

SHA = 'a' * 40
ROOT = 'https://github.com/advisories/GHSA-g3hv-75wg-m249'
ISSUE = 'https://github.com/KeyserDSoze/Omnia/issues/196'


def warning(package, version, advisory, scope):
    return {'ecosystem': 'npm', 'package': package, 'version': version,
            'advisory': advisory, 'severity': 'moderate', 'scope': scope}


def sample():
    keys = [('minimatch', '10.2.6', ROOT),
            ('@expo/fingerprint', '0.21.10', 'npm:indirect:@expo/fingerprint'),
            ('expo', '58.0.26', 'npm:indirect:expo')]
    reports = [warning(pkg, ver, advisory, scope) for pkg, ver, advisory in keys
               for scope in ('all', 'production')]
    report = {
        'version': 1, 'sourceCommit': SHA, 'status': 'pass',
        'counts': {'blocked': 0, 'accepted': 0, 'warnings': 6},
        'scanned': {'nugetProjectReports': 20, 'npmScopes': {
            'all': {'moderate': 3}, 'production': {'moderate': 3}}},
        'findings': {'blocked': [], 'accepted': [], 'warnings': reports}
    }
    review = {
        'version': 1, 'sourceIssue': ISSUE, 'owner': 'KeyserDSoze',
        'reviewedOn': '2026-11-24', 'reviewDue': '2026-12-08',
        'entries': [
            {'package': pkg, 'version': ver, 'advisory': advisory,
             'rootAdvisory': ROOT, 'scopes': ['all', 'production'],
             'upstream': 'https://github.com/expo/expo/issues/52735',
             'riskStatement': 'This is a build-tool dependency. Browser/API exploitation has not been demonstrated; a malicious local pattern may exhaust CI resources.',
             'mitigation': 'Do not accept user-controlled glob patterns or untrusted CI execution, retain hardened CI permissions, and review official Expo fixes before updating.',}
            for pkg, ver, advisory in keys]
    }
    return report, review


class ModerateAdvisoryReviewTests(unittest.TestCase):
    def assert_rejected(self, report, review, message=None, as_of=date(2026, 11, 24), sha=SHA):
        with self.assertRaises(ValueError) as caught:
            validate_review(report, review, as_of=as_of, source_sha=sha)
        if message:
            self.assertIn(message, str(caught.exception))

    def test_valid_review_covers_both_scopes_without_changing_advisories(self):
        report, review = sample()
        original = copy.deepcopy(report)
        self.assertEqual(validate_review(report, review, date(2026, 11, 24), SHA), 3)
        self.assertEqual(report, original)

    def test_missing_and_new_moderate_finding_are_blocked(self):
        report, review = sample()
        review['entries'].pop()
        self.assert_rejected(report, review, 'coverage')
        report, review = sample()
        report['findings']['warnings'].append(warning('newpkg', '1.0.0', ROOT, 'all'))
        report['counts']['warnings'] += 1
        report['scanned']['npmScopes']['all']['moderate'] += 1
        self.assert_rejected(report, review, 'coverage')

    def test_changed_version_advisory_or_scope_is_not_covered(self):
        for attr, value in (('version', '10.2.7'), ('advisory', 'npm:indirect:minimatch')):
            with self.subTest(attr=attr):
                report, review = sample()
                review['entries'][0][attr] = value
                self.assert_rejected(report, review)
        report, review = sample()
        review['entries'][0]['scopes'] = ['all']
        self.assert_rejected(report, review)

    def test_expired_future_and_overlong_reviews_fail_closed(self):
        report, review = sample()
        self.assert_rejected(report, review, 'expired', as_of=date(2026, 12, 8))
        report, review = sample()
        review['reviewDue'] = '2027-01-15'
        self.assert_rejected(report, review, '14 days')
        report, review = sample()
        review['reviewedOn'] = '2026-11-25'
        self.assert_rejected(report, review, 'future')

    def test_incomplete_or_stale_report_does_not_pass(self):
        report, review = sample()
        report['status'] = 'incomplete'
        self.assert_rejected(report, review, 'pass')
        report, review = sample()
        self.assert_rejected(report, review, 'SHA', sha='b' * 40)
        report, review = sample()
        report['scanned']['nugetProjectReports'] = 19
        self.assert_rejected(report, review, 'NuGet')

    def test_malformed_registry_broad_waiver_and_missing_risk_justification_rejected(self):
        for field, value in (('package', '*'), ('version', '*'), ('mitigation', 'safe'),
                             ('rootAdvisory', 'npm:indirect:minimatch'),
                             ('upstream', 'http://example.invalid/')):
            with self.subTest(field=field):
                report, review = sample()
                review['entries'][0][field] = value
                self.assert_rejected(report, review)
        report, review = sample()
        review['entries'][0]['waived'] = True
        self.assert_rejected(report, review, 'fields')

    def test_duplicate_review_or_warning_cannot_hide_findings(self):
        report, review = sample()
        review['entries'].append(copy.deepcopy(review['entries'][0]))
        self.assert_rejected(report, review, 'duplicate')
        report, review = sample()
        report['findings']['warnings'].append(copy.deepcopy(report['findings']['warnings'][0]))
        report['counts']['warnings'] = 7
        self.assert_rejected(report, review, 'duplicate')

    def test_audit_scope_severity_and_metadata_must_match_warning_list(self):
        report, review = sample()
        report['scanned']['npmScopes']['production']['moderate'] = 2
        self.assert_rejected(report, review, 'count')
        report, review = sample()
        report['findings']['warnings'][0]['scope'] = 'staging'
        self.assert_rejected(report, review)
        report, review = sample()
        report['findings']['warnings'].pop()
        self.assert_rejected(report, review, 'count')


if __name__ == '__main__':
    unittest.main()
