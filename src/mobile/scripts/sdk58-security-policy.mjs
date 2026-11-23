/**
 * Fail-closed regression policy for the Expo SDK58 security migration.
 *
 * This source contract supplements (never replaces) live npm audit
 * against official advisories, npm ci integrity, native builds or OIDC.
 */
const actual = (lock, name) => lock?.packages?.['node_modules/' + name]?.version;
const scalar = v => typeof v === 'string' && v.length > 0;
const same = (manifest, lock, name, declared, resolved, errors, label) => {
  if (manifest?.[declared.field]?.[name] !== declared.expected ||
      actual(lock, name) !== resolved)
    errors.push(label + ' baseline mismatch for ' + name);
};

export function checkSdk58SecurityBaseline(manifest, lock, app) {
  const errors = [];
  const deps = manifest?.dependencies;
  const dev = manifest?.devDependencies;
  if (!deps || typeof deps !== 'object' || !dev || typeof dev !== 'object')
    return ['SDK58 manifest dependencies missing'];
  if (lock?.lockfileVersion !== 3 ||
      !lock.packages || typeof lock.packages !== 'object')
    return ['SDK58 requires complete npm v3 dependency lock'];
  if (!scalar(deps.expo) || !/^\^58\.0\.\d+$/.test(deps.expo) ||
      !/^58\.\d+\.\d+$/.test(actual(lock, 'expo') || ''))
    errors.push('Expo SDK58 verified package baseline required');
  if (!scalar(deps['expo-router']) || !/^~58\.0\.\d+$/.test(deps['expo-router']) ||
      !/^58\.\d+\.\d+$/.test(actual(lock, 'expo-router') || ''))
    errors.push('Expo Router58 verified package baseline required');

  // These SDK/ABI constraints were tested together on an isolated runner.
  // Each change requires an intentional reviewed upgrade and fresh native
  // bundle, real OIDC and official advisory checks.
  for (const [name, expected, field, label] of [
    ['react-native', '0.88.1', 'dependencies', 'React Native'],
    ['react', '19.3.0', 'dependencies', 'React'],
    ['vitest', '5.1.1', 'devDependencies', 'Vitest5']
  ]) {
    if (manifest[field]?.[name] !== expected || actual(lock, name) !== expected)
      errors.push(label + ' pinned native/test baseline mismatch for ' + name);
  }
  for (const [name, expected] of [
    ['react-native-reanimated', '4.7.1'],
    ['react-native-worklets', '0.13.0']
  ]) {
    if (deps[name] !== expected || actual(lock, name) !== expected)
      errors.push('native module ABI mismatch for ' + name);
  }
  if (deps['react-native-gesture-handler'] !== '~3.3.0' ||
      actual(lock, 'react-native-gesture-handler') !== '3.3.0')
    errors.push('native module ABI mismatch for gesture handler');

  if (manifest.overrides !== undefined || manifest.resolutions !== undefined)
    errors.push('npm override/resolutions require separate security review');

  const forbidden = new Set(['query-string', 'decode-uri-component', 'tinypool']);
  for (const path of Object.keys(lock.packages)) {
    const packageName = path.split('node_modules/').at(-1);
    if (forbidden.has(packageName)) {
      if (packageName === 'tinypool')
        errors.push('tinypool must not return to Vitest5 graph: ' + path);
      else
        errors.push('vulnerable query chain must not return: ' + path);
    }
  }
  const router = lock.packages['node_modules/expo-router'];
  for (const field of ['dependencies', 'optionalDependencies', 'peerDependencies']) {
    for (const name of ['query-string', 'decode-uri-component'])
      if (router?.[field]?.[name])
        errors.push('vulnerable query chain must not be declared by Expo Router: ' + name);
  }

  if (app?.expo?.web?.output !== 'static')
    errors.push('Expo Router58 must retain static web output for GitHub Pages');
  if (app?.expo?.experiments?.baseUrl !== '/Omnia')
    errors.push('GitHub Pages baseUrl must remain /Omnia');
  return errors;
}
