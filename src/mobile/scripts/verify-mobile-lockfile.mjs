import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { checkMobileLockfile } from './mobile-lock-policy.mjs';

const base = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = name => JSON.parse(readFileSync(resolve(base, name), 'utf8'));
let errors;
try {
  errors = checkMobileLockfile(read('package.json'), read('package-lock.json'));
} catch (error) {
  console.error('Npm lockfile missing or invalid:', error.message);
  process.exit(1);
}
if (errors.length !== 0) {
  console.error('Npm lockfile validation failed:');
  for (const error of errors) console.error(' - ' + error);
  process.exit(1);
}
console.log('Npm lockfile v3 graph, provenance and root manifest verified.');
