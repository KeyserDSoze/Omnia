import assert from 'node:assert/strict';
import { test } from 'node:test';
import { checkSdk58SecurityBaseline } from './sdk58-security-policy.mjs';

const healthy = () => ({
  manifest: {
    dependencies: {
      expo: '^58.0.0', 'expo-router': '~58.0.26',
      'react-native': '0.88.1', react: '19.3.0',
      'react-native-reanimated': '4.7.1',
      'react-native-worklets': '0.13.0',
      'react-native-gesture-handler': '~3.3.0'
    },
    devDependencies: { vitest: '5.1.1' }
  },
  lock: {
    lockfileVersion: 3,
    packages: {
      'node_modules/expo': { version: '58.0.26' },
      'node_modules/expo-router': { version: '58.0.32', dependencies: {} },
      'node_modules/react-native': { version: '0.88.1' },
      'node_modules/react': { version: '19.3.0' },
      'node_modules/vitest': { version: '5.1.1' }
    }
  },
  app: { expo: { web: { output: 'static' }, experiments: { baseUrl: '/Omnia' } } }
});
const check = state => checkSdk58SecurityBaseline(state.manifest, state.lock, state.app);

test('accepts reviewed Expo58, React Native 0.88, Vitest5, static Pages graph', () => {
  assert.deepEqual(check(healthy()), []);
});

test('rejects a lock/manifest downgrade to Expo SDK57 or Expo Router57', () => {
  const a = healthy(); a.manifest.dependencies.expo = '~57.0.10';
  assert.match(check(a).join(';'), /Expo SDK58/);
  const b = healthy(); b.lock.packages['node_modules/expo-router'].version = '57.0.32';
  assert.match(check(b).join(';'), /Expo Router58/);
});

test('rejects nested vulnerable query-string or decode-uri-component', () => {
  for (const name of ['query-string', 'decode-uri-component']) {
    const s = healthy();
    s.lock.packages['node_modules/other/node_modules/' + name] = { version: '0.2.2' };
    assert.match(check(s).join(';'), /vulnerable query chain/, name);
  }
  const b = healthy();
  b.lock.packages['node_modules/expo-router'].dependencies['query-string'] = '^7.1.3';
  assert.match(check(b).join(';'), /vulnerable query chain/);
});

test('rejects a return to the critical tinypool/Vitest3 graph', () => {
  const a = healthy(); a.lock.packages['node_modules/tinypool'] = { version: '1.1.1' };
  assert.match(check(a).join(';'), /tinypool/);
  const b = healthy(); b.manifest.devDependencies.vitest = '^3.2.4';
  assert.match(check(b).join(';'), /Vitest5/);
});

test('rejects unreviewed native ABI drift or npm overrides', () => {
  const a = healthy(); a.lock.packages['node_modules/react-native'].version = '0.86.2';
  assert.match(check(a).join(';'), /React Native/);
  const b = healthy(); b.manifest.overrides = { 'decode-uri-component': '0.5.1' };
  assert.match(check(b).join(';'), /override/);
});

test('refuses Router server-rendering defaults and lost GitHub Pages base path', () => {
  const a = healthy(); delete a.app.expo.web.output;
  assert.match(check(a).join(';'), /static/);
  const b = healthy(); b.app.expo.experiments.baseUrl = '/';
  assert.match(check(b).join(';'), /baseUrl/);
});
