#!/usr/bin/env python3
"""Read-only inventory of locked, restored NuGet packages; not legal clearance."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import unquote
from xml.etree import ElementTree as ET
import zipfile

NOTICE = {'MIT', 'ISC', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', '0BSD'}
ID = re.compile(r'[a-z0-9][a-z0-9_.-]*\Z')
VERSION = re.compile(r'(\d+(?:\.\d+){0,3})(-[A-Za-z0-9.-]+)?(?:\+[A-Za-z0-9.-]+)?\Z')
NAMESPACE = re.compile(r'http://schemas\.microsoft\.com/packaging/\d{4}/\d{2}/nuspec\.xsd\Z')
MAX_METADATA = 4 * 1024 * 1024


def normalized_version(value):
    match = VERSION.fullmatch(value) if isinstance(value, str) else None
    if not match:
        raise ValueError('invalid NuGet version')
    parts = [int(x) for x in match[1].split('.')]
    parts += [0] * (3 - len(parts))
    if len(parts) == 4 and parts[-1] == 0:
        parts.pop()
    return '.'.join(map(str, parts)) + (match[2] or '').lower()


def checked_hash(value):
    try:
        raw = base64.b64decode(value, validate=True)
    except (ValueError, TypeError) as exc:
        raise ValueError('invalid content hash') from exc
    if len(raw) != 64 or base64.b64encode(raw).decode() != value:
        raise ValueError('invalid content hash')
    return value


def signed_hash(path):
    # .NET 10/NuGet 7 uses the NuGet content hash, excluding the signature.
    # A raw ZIP SHA-512 is NOT an equivalent hash for signed packages.
    env = {**os.environ, 'DOTNET_CLI_UI_LANGUAGE': 'en-US', 'DOTNET_NOLOGO': '1'}
    try:
        result = subprocess.run(['dotnet', 'nuget', 'verify', str(path), '--verbosity', 'normal'],
                                capture_output=True, text=True, timeout=90, env=env, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise ValueError('NuGet signature verification unavailable') from exc
    hashes = re.findall(r'^\s*Content hash:\s*([A-Za-z0-9+/=]+)\s*$', result.stdout, re.MULTILINE)
    if result.returncode or len(hashes) != 1:
        raise ValueError('NuGet signature verification or content hash failed')
    return checked_hash(hashes[0])


def safe_member(name):
    decoded = unquote(name)
    if (not decoded or '\\' in decoded or ':' in decoded or any(ord(c) < 32 for c in decoded)
            or any(p in ('', '.', '..') for p in decoded.rstrip('/').split('/'))):
        raise ValueError('unsafe archive member path')
    return decoded.rstrip('/')


def read_small(archive, info):
    if info.is_dir() or info.file_size > MAX_METADATA or (info.external_attr >> 16) & 0o170000 == 0o120000:
        raise ValueError('invalid or oversized metadata member')
    with archive.open(info) as stream:
        raw = stream.read(MAX_METADATA + 1)
    if len(raw) > MAX_METADATA:
        raise ValueError('oversized metadata member')
    return raw


def metadata(archive, members, name, version):
    manifests = [info for key, info in members.items() if '/' not in key and key.endswith('.nuspec')]
    if len(manifests) != 1:
        raise ValueError('expected one root nuspec')
    try:
        text = read_small(archive, manifests[0]).decode('utf-8-sig')
        if '<!DOCTYPE' in text.upper() or '<!ENTITY' in text.upper():
            raise ValueError('unsupported XML declaration')
        xml = ET.fromstring(text)
    except (UnicodeError, ET.ParseError) as exc:
        raise ValueError('invalid nuspec XML') from exc
    ns = xml.tag[1:].split('}', 1)[0] if xml.tag.startswith('{') else ''
    if ns and not NAMESPACE.fullmatch(ns):
        raise ValueError('unexpected nuspec namespace')
    prefix = '{' + ns + '}' if ns else ''
    if xml.tag != prefix + 'package':
        raise ValueError('invalid nuspec root')

    def single(parent, tag, required=False):
        nodes = parent.findall(prefix + tag)
        if len(nodes) > 1 or (required and len(nodes) != 1):
            raise ValueError('missing or duplicate nuspec metadata: ' + tag)
        return nodes[0] if nodes else None

    def value(node):
        if node is None:
            return None
        if len(node) or not node.text or not node.text.strip() or len(node.text) > 4096:
            raise ValueError('invalid nuspec metadata value')
        return node.text

    meta = single(xml, 'metadata', True)
    if value(single(meta, 'id', True)).lower() != name or normalized_version(value(single(meta, 'version', True))) != version:
        raise ValueError('nuspec identity mismatch')
    license_node = single(meta, 'license')
    legacy = single(meta, 'licenseUrl')
    row = {'licenseKind': 'missing', 'declaredLicense': None, 'triage': 'additional-review'}
    if license_node is not None:
        declaration = value(license_node)
        kind = license_node.get('type')
        if kind not in ('expression', 'file'):
            raise ValueError('unknown nuspec license type')
        row.update(licenseKind=kind, declaredLicense=declaration)
        if kind == 'expression' and declaration in NOTICE:
            row['triage'] = 'notice-review'
        if kind == 'file':
            key = safe_member(declaration).casefold()
            if key not in members:
                raise ValueError('declared license file missing')
            row['licenseFileSha256'] = hashlib.sha256(read_small(archive, members[key])).hexdigest()
            row['licenseArchiveMember'] = members[key].filename
    elif legacy is not None:
        row.update(licenseKind='legacy-url', declaredLicense=value(legacy))
    return row


def inspect_package(path, name, version, expected):
    if not path.is_file() or path.is_symlink():
        raise ValueError('locked package archive missing or symlinked')
    checked_hash(expected)
    with path.open('rb') as stream:
        archive_sha = hashlib.file_digest(stream, 'sha256').hexdigest()
    try:
        with zipfile.ZipFile(path) as archive:
            members = {}
            for info in archive.infolist():
                key = safe_member(info.filename).casefold()
                if key in members:
                    raise ValueError('ambiguous or duplicate archive member')
                members[key] = info
            signed = '.signature.p7s' in members
            if signed:
                actual = signed_hash(path)
            else:
                with path.open('rb') as stream:
                    actual = base64.b64encode(hashlib.file_digest(stream, 'sha512').digest()).decode()
            if actual != expected:
                raise ValueError('NuGet content hash mismatch')
            row = metadata(archive, members, name, version)
    except (zipfile.BadZipFile, RuntimeError, NotImplementedError) as exc:
        raise ValueError('invalid NuGet package archive') from exc
    return {'name': name, 'version': version, 'contentHash': expected,
            'archiveSha256': archive_sha, 'hashMethod': 'nuget-verify' if signed else 'raw-sha512', **row}


def build_report(bom, cache, source_sha, project_count):
    if project_count != 20 or not bom.get('components'):
        raise ValueError('expected complete twenty-project lock inventory')
    if source_sha != 'local' and not re.fullmatch(r'[0-9a-f]{40}', source_sha):
        raise ValueError('invalid source commit')
    rows = []
    seen = set()
    counts = {'packages': 0, 'notice-review': 0, 'additional-review': 0,
              'expression': 0, 'file': 0, 'legacy-url': 0, 'missing': 0,
              'nuget-verify': 0, 'raw-sha512': 0}
    for component in sorted(bom['components'], key=lambda c: (c['name'], c['version'])):
        name, version = component['name'], component['version']
        if not ID.fullmatch(name) or normalized_version(version) != version or (name, version) in seen:
            raise ValueError('invalid or duplicate locked package identity')
        seen.add((name, version))
        checksum, = component['hashes']
        if checksum['alg'] != 'SHA-512':
            raise ValueError('missing locked content hash')
        expected = checked_hash(base64.b64encode(bytes.fromhex(checksum['content'])).decode())
        owners, = [p['value'] for p in component['properties'] if p['name'] == 'omnia:lock-owners']
        path = cache / name / version / f'{name}.{version}.nupkg'
        if not path.resolve().is_relative_to(cache.resolve()):
            raise ValueError('package archive escapes restore cache')
        try:
            row = inspect_package(path, name, version, expected)
        except (ValueError, OSError) as exc:
            raise ValueError(f'{name}@{version}: {exc}') from exc
        row['lockOwners'] = sorted(owners.split(';'))
        rows.append(row)
        for key in ('packages', row['triage'], row['licenseKind'], row['hashMethod']):
            counts[key] += 1
    return {'version': 1, 'sourceCommit': source_sha, 'status': 'inventory-complete',
            'projectCount': project_count, 'counts': counts, 'packages': rows}


def main():
    from generate_locked_sbom import nuget_bom, project_paths
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cache', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    try:
        report = build_report(nuget_bom(root), args.cache, os.environ.get('GITHUB_SHA', 'local'), len(project_paths(root)))
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    except (ValueError, OSError, TypeError, KeyError) as exc:
        print('NUGET_LICENSE_INVENTORY_FAILED: ' + str(exc), file=sys.stderr)
        return 1
    print('NUGET_LICENSE_INVENTORY: ' + json.dumps(report['counts'], sort_keys=True))
    return 0


if __name__ == '__main__':
    sys.exit(main())
