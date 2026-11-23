/**
 * Validate the checked-in npm lock graph before installing Expo dependencies.
 * No network, no modification, no credential access.
 */
export function checkMobileLockfile(manifest, lock) {
  const errors = [];
  if (!manifest || typeof manifest !== 'object' || Array.isArray(manifest))
    return ['package.json must be an object'];
  if (!lock || typeof lock !== 'object' || Array.isArray(lock))
    return ['package-lock.json must be a JSON object'];
  if (lock.lockfileVersion !== 3)
    errors.push('lockfileVersion must be 3 (npm 10)');
  if (lock.name !== manifest.name || lock.version !== manifest.version)
    errors.push('lockfile identity does not match package.json');
  const packages = lock.packages;
  if (!packages || typeof packages !== 'object' || Array.isArray(packages))
    return [...errors, 'lockfile packages graph is missing'];
  const root = packages[''];
  if (!root || typeof root !== 'object')
    return [...errors, 'lockfile root package is missing'];
  for (const field of ['dependencies', 'devDependencies']) {
    const left = manifest[field] ?? {};
    const right = root[field] ?? {};
    const names = new Set([...Object.keys(left), ...Object.keys(right)]);
    for (const name of names)
      if (left[name] !== right[name])
        errors.push('lockfile root ' + field + ' differs for ' + name);
  }
  if (!packages['node_modules/expo'])
    errors.push('resolved Expo dependency graph is missing');
  if (!Object.keys(packages).some(path => path.endsWith('/@expo/inline-modules')))
    errors.push('resolved Expo inline-modules dependency is missing');

  for (const [path, pkg] of Object.entries(packages)) {
    if (!path || !path.startsWith('node_modules/')) continue;
    if (!pkg || typeof pkg !== 'object') {
      errors.push('invalid lock entry ' + path);
      continue;
    }
    if (pkg.link) {
      errors.push('unexpected symlink in lockfile ' + path);
      continue;
    }
    if (typeof pkg.resolved !== 'string' ||
        !pkg.resolved.startsWith('https://registry.npmjs.org/')) {
      errors.push('non-npm or unpinned tarball in ' + path);
      continue;
    }
    if (typeof pkg.integrity !== 'string' ||
        !/^sha512-[A-Za-z0-9+/]+={0,2}$/.test(pkg.integrity))
      errors.push('missing or invalid SHA-512 tarball integrity for ' + path);
  }
  return errors;
}
