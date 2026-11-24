#!/usr/bin/env python3
"""Require time-bounded review of npm MODERATE findings from the live SCA report.

Reviews are NOT waivers: the original scanner findings remain unchanged and
HIGH/CRITICAL still block in run_live_advisory_audit.py. Standard library only.
"""
import argparse
from datetime import date, datetime, timezone
import json
from pathlib import Path
import re
import sys
from urllib.parse import urlparse

ISSUE = 'https://github.com/KeyserDSoze/Omnia/issues/196'
ENTRY_FIELDS = {'package', 'version', 'advisory', 'rootAdvisory', 'scopes',
                'upstream', 'riskStatement', 'mitigation'}
REGISTRY_FIELDS = {'version', 'sourceIssue', 'owner', 'reviewedOn',
                   'reviewDue', 'entries'}
SCOPE = {'all', 'production'}
SHA = re.compile(r'[0-9a-f]{40}\Z')
PACKAGE = re.compile(r'(?:@[-a-z0-9._]+/)?[-a-z0-9._]+\Z')
VERSION = re.compile(r'[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?\Z')
ROOT = re.compile(r'https://github\.com/advisories/GHSA-[a-z0-9-]+\Z')


def _date(value: object, name: str) -> date:
    if not isinstance(value, str) or not re.fullmatch(r'\d{4}-\d{2}-\d{2}', value):
        raise ValueError(f'{name} must be exact UTC YYYY-MM-DD')
    try:
        return date.fromisoformat(value)
    except ValueError as exc:
        raise ValueError(f'{name} has an invalid calendar date') from exc


def _url(value: object) -> bool:
    if not isinstance(value, str):
        return False
    parsed = urlparse(value)
    return (parsed.scheme == 'https' and parsed.hostname == 'github.com'
            and not parsed.username and not parsed.password
            and bool(parsed.path.strip('/')))


def validate_review(report: object, registry: object,
                    as_of: date, source_sha: str) -> int:
    """Return reviewed advisory identities, or deny unexpected/stale warnings."""
    if not isinstance(report, dict) or report.get('version') != 1 or report.get('status') != 'pass':
        raise ValueError('SCA report must be a completed pass before review')
    if not isinstance(source_sha, str) or not SHA.fullmatch(source_sha) or report.get('sourceCommit') != source_sha:
        raise ValueError('audit source SHA does not match actual CI commit')
    scanned = report.get('scanned')
    if not isinstance(scanned, dict) or scanned.get('nugetProjectReports') != 20:
        raise ValueError('NuGet source audits must cover all 20 projects')
    scopes = scanned.get('npmScopes')
    if not isinstance(scopes, dict) or set(scopes) != SCOPE:
        raise ValueError('npm audit must contain both scopes')
    for scope in SCOPE:
        if not isinstance(scopes[scope], dict) or type(scopes[scope].get('moderate')) is not int:
            raise ValueError('npm moderate count missing')

    findings = report.get('findings')
    if not isinstance(findings, dict) or set(findings) != {'accepted', 'blocked', 'warnings'} or not isinstance(findings['warnings'], list):
        raise ValueError('complete SCA warning details required')
    if findings['blocked']:
        raise ValueError('SCA blocked findings cannot be reviewed away')
    counts = report.get('counts')
    if not isinstance(counts, dict) or counts.get('warnings') != len(findings['warnings']):
        raise ValueError('SCA warning count mismatches warnings')
    expected = set()
    count_by_scope = {scope: set() for scope in SCOPE}
    for warning in findings['warnings']:
        if not isinstance(warning, dict):
            raise ValueError('malformed advisory warning')
        if warning.get('ecosystem') != 'npm' or warning.get('severity') != 'moderate':
            continue  # Other warnings stay in the original SCA report.
        scope = warning.get('scope')
        if scope not in SCOPE:
            raise ValueError('unrecognised npm review scope')
        key = (warning.get('package'), warning.get('version'), warning.get('advisory'), scope)
        if any(not isinstance(x, str) or not x for x in key):
            raise ValueError('incomplete npm advisory identity')
        if key in expected:
            raise ValueError('duplicate npm moderate warning')
        expected.add(key)
        count_by_scope[scope].add((warning['package'], warning['version']))
    for scope in SCOPE:
        if len(count_by_scope[scope]) != scopes[scope]['moderate']:
            raise ValueError(f'npm moderate scanner count mismatch for {scope}')

    if not isinstance(registry, dict) or set(registry) != REGISTRY_FIELDS or registry['version'] != 1:
        raise ValueError('review registry has unsupported fields or version')
    if registry['sourceIssue'] != ISSUE or registry['owner'] != 'KeyserDSoze':
        raise ValueError('review owner or issue missing')
    reviewed = _date(registry['reviewedOn'], 'reviewedOn')
    due = _date(registry['reviewDue'], 'reviewDue')
    if reviewed > as_of:
        raise ValueError('review date in the future')
    if due <= reviewed or (due - reviewed).days > 14:
        raise ValueError('review cycle must be at most 14 days')
    if as_of >= due:
        raise ValueError('moderate review expired; re-evaluate current advisories')
    entries = registry['entries']
    if not isinstance(entries, list) or not entries or len(entries) > 64:
        raise ValueError('review entries missing or too numerous')
    actual = set()
    unique = set()
    for entry in entries:
        if not isinstance(entry, dict) or set(entry) != ENTRY_FIELDS:
            raise ValueError('review entry unsupported fields: no waivers/ignores allowed')
        pkg, version, advisory = (entry[x] for x in ('package', 'version', 'advisory'))
        if not isinstance(pkg, str) or not PACKAGE.fullmatch(pkg):
            raise ValueError('invalid or wildcard review package')
        if not isinstance(version, str) or not VERSION.fullmatch(version):
            raise ValueError('invalid or wildcard review version')
        if not isinstance(advisory, str) or not (ROOT.fullmatch(advisory) or advisory == f'npm:indirect:{pkg}'):
            raise ValueError('review advisory must be exact URL or indirect marker')
        if not isinstance(entry['rootAdvisory'], str) or not ROOT.fullmatch(entry['rootAdvisory']):
            raise ValueError('review needs canonical root GHSA URL')
        if advisory.startswith('https://') and entry['rootAdvisory'] != advisory:
            raise ValueError('direct GHSA must be its own root advisory')
        if not _url(entry['upstream']):
            raise ValueError('untrusted upstream reference')
        for field in ('riskStatement', 'mitigation'):
            if not isinstance(entry[field], str) or len(entry[field].strip()) < 90:
                raise ValueError(f'review requires substantial {field}')
        scopes_list = entry['scopes']
        if not isinstance(scopes_list, list) or not scopes_list or len(scopes_list) != len(set(scopes_list)) or not set(scopes_list) <= SCOPE:
            raise ValueError('review has unsupported scopes')
        identity = (pkg, version, advisory)
        if identity in unique:
            raise ValueError('duplicate moderate review entry')
        unique.add(identity)
        actual.update((pkg, version, advisory, scope) for scope in scopes_list)
    if actual != expected:
        raise ValueError('moderate advisory coverage mismatch: new, stale or changed warning requires review')
    return len(unique)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--reviews', type=Path, required=True)
    parser.add_argument('--source-sha', required=True)
    args = parser.parse_args()
    try:
        report = json.loads(args.report.read_text(encoding='utf-8'))
        reviews = json.loads(args.reviews.read_text(encoding='utf-8'))
        count = validate_review(report, reviews, datetime.now(timezone.utc).date(), args.source_sha)
    except (OSError, ValueError, TypeError, KeyError) as exc:
        print('MODERATE_REVIEW_FAILED: ' + str(exc), file=sys.stderr)
        return 1
    print(f'MODERATE_REVIEW_OK: {count} exact warning identities under unexpired review; no waivers')
    return 0


if __name__ == '__main__':
    sys.exit(main())
