import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkMobileLockfile } from './mobile-lock-policy.mjs';

const hash = 'sha512-YWJjZGVmZ2hpamtsbW5vcA==';
const entry = name => ({
  version: '1.0.0',
  resolved: 'https://registry.npmjs.org/' + name + '/-/' + name.split('/').at(-1) + '-1.0.0.tgz',
  integrity: hash
});
const manifest = { name: '@omnia/mobile', version: '0.1.0',
  dependencies: { expo: '^58.0.0' }, devDependencies: { vitest: '5.1.1' } };
const valid = () => ({
  name: manifest.name, version: manifest.version, lockfileVersion: 3,
  packages: {
    '': { name: manifest.name, version: manifest.version,
      dependencies: { ...manifest.dependencies }, devDependencies: { ...manifest.devDependencies } },
    'node_modules/expo': entry('expo'),
    'node_modules/@expo/inline-modules': entry('@expo/inline-modules')
  }
});
test('accepts a complete exact npm v3 manifest and SHA-512 registry graph', () => {
  assert.deepEqual(checkMobileLockfile(manifest, valid()), []);
});
test('rejects a missing, stale or incomplete lock before npm install', () => {
  assert.ok(checkMobileLockfile(manifest, null).length > 0);
  const wrong = valid(); wrong.packages[''].dependencies.expo = '~58.0.0';
  assert.match(checkMobileLockfile(manifest, wrong).join(','), /expo/);
  const absent = valid(); delete absent.packages['node_modules/expo'];
  assert.match(checkMobileLockfile(manifest, absent).join(','), /Expo/);
  const invalidVersion = valid(); invalidVersion.lockfileVersion = 1;
  assert.match(checkMobileLockfile(manifest, invalidVersion).join(','), /lockfileVersion/);
});
test('rejects untrusted URLs, unchecked tarballs and local symlinks', () => {
  const hostile = valid();
  hostile.packages['node_modules/expo'].resolved = 'https://example.invalid/expo.tgz';
  assert.match(checkMobileLockfile(manifest, hostile).join(','), /non-npm/);
  const noDigest = valid();
  delete noDigest.packages['node_modules/expo'].integrity;
  assert.match(checkMobileLockfile(manifest, noDigest).join(','), /SHA-512/);
  const link = valid(); link.packages['node_modules/expo'] = { link: true };
  assert.match(checkMobileLockfile(manifest, link).join(','), /symlink/);
});
