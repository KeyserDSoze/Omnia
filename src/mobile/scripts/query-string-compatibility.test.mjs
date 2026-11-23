import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { test } from 'node:test';

const require = createRequire(import.meta.url);

test('Expo Router query-string 7 CommonJS parser resolves and decodes URI safely', () => {
  // Expo Router SDK57 depends on query-string 7.x (CommonJS), which
  // imports decode-uri-component. Forcing an ESM-only transitive major
  // can make this fail at runtime even when Metro compilation succeeds.
  const queryString = require('query-string');
  const actual = queryString.parse('?term=Omnia%20App&token=%C3%A8');
  assert.equal(actual.term, 'Omnia App');
  assert.equal(actual.token, 'è');
  assert.equal(typeof queryString.stringify, 'function');
});
