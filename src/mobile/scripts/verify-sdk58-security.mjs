import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkSdk58SecurityBaseline } from './sdk58-security-policy.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const load = path => JSON.parse(readFileSync(resolve(root, path), 'utf8'));
let errors;
try {
  errors = checkSdk58SecurityBaseline(
    load('package.json'), load('package-lock.json'), load('app.json')
  );
} catch (error) {
  console.error('Expo SDK58 security baseline input missing/malformed:', error.message);
  process.exit(1);
}
if (errors.length) {
  console.error('Expo SDK58 security regression policy FAILED:');
  for (const error of errors) console.error(' - ' + error);
  process.exit(1);
}
console.log('Expo SDK58/Router58, RN 0.88.1, Vitest5, static Pages and CVE regression policy PASS.');
