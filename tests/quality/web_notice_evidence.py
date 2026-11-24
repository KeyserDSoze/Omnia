#!/usr/bin/env python3
"""Hash installed npm notice candidates and the real Web export, not legal clearance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys

NAME = r'(?:@[a-zA-Z0-9_.-]+/)?[a-zA-Z0-9_.-]+'
PACKAGE_PATH = re.compile(r'(?:node_modules/' + NAME + r'/)*node_modules/' + NAME + r'\Z')
NOTICE = re.compile(r'(?:licen[cs]e|copying|notice|copyright|unlicense)(?:[._-].*)?\Z', re.I)


def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('duplicate JSON key')
        result[key] = value
    return result


def checked_path(root, relative):
    parts = relative.split('/')
    if any(p in ('', '.', '..') or '\\' in p or any(ord(c) < 32 for c in p) for p in parts):
        raise ValueError('unsafe relative path')
    path = root
    for part in parts:
        path = path / part
        if path.is_symlink():
            raise ValueError('symlink in evidence path')
    return path


def file_evidence(path, relative):
    if path.is_symlink() or not stat.S_ISREG(path.stat().st_mode):
        raise ValueError('expected regular evidence file')
    with path.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    return {'path': relative, 'sha256': digest, 'size': path.stat().st_size}


def build_report(root, raw, sha):
    if sha != 'local' and not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise ValueError('invalid source commit')
    lock = json.loads(raw, object_pairs_hook=unique)
    if not isinstance(lock, dict) or lock.get('lockfileVersion') != 3:
        raise ValueError('npm v3 lock required')
    packages = lock.get('packages')
    if not isinstance(packages, dict) or not isinstance(packages.get(''), dict) or len(packages) < 2:
        raise ValueError('missing package graph')
    rows = []
    counts = dict.fromkeys(('locked', 'installed', 'optional-absent', 'no-root-notice', 'declaration-differs', 'notice-files', 'web-files'), 0)
    for relative, pkg in sorted(packages.items()):
        if not relative:
            continue
        if not PACKAGE_PATH.fullmatch(relative) or not isinstance(pkg, dict) or pkg.get('link'):
            raise ValueError('invalid package entry')
        path = checked_path(root, relative)
        name = pkg.get('name', relative.rsplit('node_modules/', 1)[-1])
        version = pkg.get('version')
        if not isinstance(name, str) or not re.fullmatch(NAME, name) or not isinstance(version, str) or not version:
            raise ValueError('invalid locked identity')
        flags = {k: pkg.get(k, False) for k in ('dev', 'optional', 'devOptional')}
        if any(type(v) is not bool for v in flags.values()):
            raise ValueError('invalid npm flags')
        row = {'path': relative, 'name': name, 'version': version,
               'lockedLicense': pkg.get('license'), 'noticeFiles': [], **flags}
        if not path.exists():
            if not (flags['optional'] or flags['devOptional']):
                raise ValueError('required package absent: ' + relative)
            row['status'] = 'optional-absent'
        else:
            manifest_path = checked_path(root, relative + '/package.json')
            if not path.is_dir() or not manifest_path.is_file():
                raise ValueError('installed manifest missing: ' + relative)
            manifest = json.loads(manifest_path.read_bytes(), object_pairs_hook=unique)
            if not isinstance(manifest, dict) or manifest.get('name') != name or manifest.get('version') != version:
                raise ValueError('installed identity mismatch: ' + relative)
            row['status'] = 'installed'
            row['installedLicense'] = manifest.get('license')
            if row['installedLicense'] != row['lockedLicense']:
                counts['declaration-differs'] += 1
            for candidate in sorted(path.iterdir()):
                if NOTICE.fullmatch(candidate.name):
                    checked_path(root, relative + '/' + candidate.name)
                    if candidate.is_dir():
                        continue  # root files only, directories are outside scope
                    row['noticeFiles'].append(file_evidence(candidate, candidate.name))
            counts['notice-files'] += len(row['noticeFiles'])
            if not row['noticeFiles']:
                counts['no-root-notice'] += 1
        counts[row['status']] += 1
        counts['locked'] += 1
        rows.append(row)
    dist = checked_path(root, 'dist')
    if not dist.is_dir():
        raise ValueError('Web export missing')
    web = []
    for directory, dirs, files in os.walk(dist, followlinks=False):
        for item in sorted(dirs + files):
            path = Path(directory) / item
            relative = path.relative_to(dist).as_posix()
            checked_path(dist, relative)
            if item in files:
                web.append(file_evidence(path, relative))
    web.sort(key=lambda row: row['path'])
    if not any(r['path'] == 'index.html' for r in web) or not any(r['path'].endswith('.js') for r in web):
        raise ValueError('Web export needs index.html and JavaScript')
    counts['web-files'] = len(web)
    return {'version': 1, 'sourceCommit': sha, 'status': 'evidence-complete',
            'lockSha256': hashlib.sha256(raw).hexdigest(), 'counts': counts,
            'packages': rows, 'webFiles': web}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        root = args.root.resolve()
        report = build_report(root, checked_path(root, 'package-lock.json').read_bytes(), os.environ.get('GITHUB_SHA', 'local'))
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    except (OSError, ValueError, TypeError) as exc:
        print('WEB_NOTICE_EVIDENCE_FAILED: ' + str(exc), file=sys.stderr)
        return 1
    print('WEB_NOTICE_EVIDENCE: ' + json.dumps(report['counts'], sort_keys=True))
    return 0


if __name__ == '__main__':
    sys.exit(main())
