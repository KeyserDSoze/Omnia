import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { test } from 'node:test';

const require = createRequire(import.meta.url);

test('Expo Router URL parsing is compatible with the locked SDK generation', () => {
  const lock = require('../package-lock.json');
  const router = lock.packages?.['node_modules/expo-router'];
  assert.ok(router?.version, 'Expo Router must be present in the exact npm lock');

  if (router.version.startsWith('57.')) {
    // SDK57 Router requires query-string 7 (CommonJS). Replacing only
    // decode-uri-component with ESM 0.5 breaks parsing at runtime.
    const queryString = require('query-string');
    const parsed = queryString.parse('?term=Omnia%20App&token=%C3%A8');
    assert.equal(parsed.term, 'Omnia App');
    assert.equal(parsed.token, 'è');
    assert.equal(typeof queryString.stringify, 'function');
    return;
  }

  if (router.version.startsWith('58.')) {
    // Expo SDK58 removes this transitive vulnerable route chain.
    // Do not silently resurrect query-string 7 in Router itself.
    assert.equal(router.dependencies?.['query-string'], undefined,
      'SDK58 Router must not carry vulnerable query-string 7');
    assert.equal(router.dependencies?.['decode-uri-component'], undefined);
    const params = new URLSearchParams('?term=Omnia%20App&token=%C3%A8');
    assert.equal(params.get('term'), 'Omnia App');
    assert.equal(params.get('token'), 'è');
    return;
  }

  assert.fail('Unknown Expo Router major: explicitly review navigation upgrade');
});
