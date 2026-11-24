#!/usr/bin/env python3
"""Inventory declared npm lockfile licenses; not a legal clearance or bundle scan."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys

# Exact strings observed in the reviewed lock; not a general SPDX parser.
# Even notice-review requires verification of upstream notices before release.
NOTICE = {'MIT', 'ISC', 'Apache-2.0', 'BSD-3-Clause', 'BSD-2-Clause', '0BSD'}
ADDITIONAL = {'(MIT OR CC0-1.0)', 'Python-2.0', 'Unlicense', 'CC-BY-4.0',
              '(MIT OR Apache-2.0)', 'BlueOak-1.0.0', 'MPL-2.0',
              '(BSD-3-Clause OR GPL-2.0)'}
NAME = r'(?:@[a-zA-Z0-9_.-]+/)?[a-zA-Z0-9_.-]+'
PATH = re.compile(r'(?:node_modules/' + NAME + r'/)*node_modules/' + NAME + r'\Z')


def build_inventory(lock):
    if not isinstance(lock, dict) or lock.get('lockfileVersion') != 3:
        raise ValueError('npm lockfile v3 required')
    packages = lock.get('packages')
    if not isinstance(packages, dict) or not isinstance(packages.get(''), dict) or len(packages) < 2:
        raise ValueError('root and nonempty npm dependency graph required')
    counts = {'occurrences': 0, 'notice-review': 0, 'additional-review': 0, 'unclassified': 0}
    rows = []
    for path, pkg in sorted(packages.items()):
        if path == '':
            continue
        if not PATH.fullmatch(path) or not isinstance(pkg, dict) or pkg.get('link'):
            raise ValueError('unexpected lock package entry')
        name = pkg.get('name', path.rsplit('node_modules/', 1)[-1])
        version = pkg.get('version')
        if not isinstance(name, str) or not re.fullmatch(NAME, name) or not isinstance(version, str) or not version.strip():
            raise ValueError('missing or malformed package identity')
        flags = {key: pkg.get(key, False) for key in ('dev', 'optional', 'devOptional')}
        if any(type(flag) is not bool for flag in flags.values()):
            raise ValueError('malformed npm scope flag')
        license_value = pkg.get('license')
        if license_value is not None and not isinstance(license_value, str):
            raise ValueError('malformed license metadata')
        triage = ('notice-review' if license_value in NOTICE else
                  'additional-review' if license_value in ADDITIONAL else 'unclassified')
        rows.append({'path': path, 'name': name, 'version': version,
                     'declaredLicense': license_value, 'triage': triage, **flags})
        counts[triage] += 1
        counts['occurrences'] += 1
    return {'status': 'blocked' if counts['unclassified'] else 'inventory-complete',
            'counts': counts, 'packages': rows}


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('duplicate JSON key')
        result[key] = value
    return result


def make_report(raw, source_sha):
    if source_sha != 'local' and not re.fullmatch(r'[0-9a-f]{40}', source_sha):
        raise ValueError('invalid source commit SHA')
    data = json.loads(raw, object_pairs_hook=_unique)
    return {'version': 1, 'sourceCommit': source_sha,
            'source': 'src/mobile/package-lock.json',
            'lockSha256': hashlib.sha256(raw).hexdigest(), **build_inventory(data)}


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument('--lock', type=Path, required=True)
    cli.add_argument('--output', type=Path, required=True)
    args = cli.parse_args()
    sha = os.environ.get('GITHUB_SHA', 'local')
    try:
        report = make_report(args.lock.read_bytes(), sha)
    except (OSError, ValueError, TypeError) as exc:
        print('NPM_LICENSE_INVENTORY_INCOMPLETE: ' + type(exc).__name__, file=sys.stderr)
        return 1
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, sort_keys=True, indent=2) + '\n', encoding='utf-8')
    print('NPM_LICENSE_INVENTORY: ' + report['status'] + ' ' + json.dumps(report['counts'], sort_keys=True))
    return 1 if report['status'] == 'blocked' else 0


if __name__ == '__main__':
    sys.exit(main())
