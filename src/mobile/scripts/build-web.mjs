// `npm run build:web`: Expo export, SEO files, then the version manifest and the stamped service worker.
// EXPO_PUBLIC_TAO_VERSION is inlined into the bundle so the app knows which version it is.
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const version = JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8')).version;
const env = { ...process.env, EXPO_PUBLIC_TAO_VERSION: version };

for (const command of ['npx expo export -p web', 'node scripts/seo.mjs', 'node scripts/release.mjs']) {
  const result = spawnSync(command, { cwd: root, env, stdio: 'inherit', shell: true });
  if (result.status !== 0) process.exit(result.status ?? 1);
}
