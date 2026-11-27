// After `expo export`: writes version.json (version, build, release notes) and stamps the service worker.
// The build id is the hash of the entry bundle, so it changes exactly when the web app changes.
import { readdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const MAX_RELEASES = 8;

/** The id of the entry bundle in the file names of an export, or null. */
export function buildIdOf(fileNames) {
  for (const name of fileNames) {
    const match = /^entry-([a-f0-9]{6,64})\.js$/.exec(name);
    if (match) return match[1];
  }
  return null;
}

/** version.json: what is current and what changed lately, newest release first. */
export function versionManifest({ version, build, releases, now = new Date() }) {
  const sorted = [...releases].sort((a, b) => compare(b.version, a.version));
  if (!sorted.length || sorted[0].version !== version)
    throw new Error(`release-notes.json must start with the app version ${version}`);
  return { version, build, builtAt: now.toISOString(), releases: sorted.slice(0, MAX_RELEASES) };
}

function compare(a, b) {
  const x = a.split('-')[0].split('.').map(Number), y = b.split('-')[0].split('.').map(Number);
  for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] - y[i];
  return 0;
}

export function stampWorker(source, build) {
  if (!source.includes('__TAO_BUILD__')) throw new Error('The service worker has no build marker');
  return source.replace('__TAO_BUILD__', build);
}

async function main() {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const dist = path.join(root, 'dist');
  const pkg = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
  const releases = JSON.parse(await readFile(path.join(root, 'release-notes.json'), 'utf8'));
  const build = buildIdOf(await readdir(path.join(dist, '_expo', 'static', 'js', 'web')));
  if (!build) throw new Error('No entry bundle in dist/_expo/static/js/web');
  await writeFile(path.join(dist, 'version.json'),
    JSON.stringify(versionManifest({ version: pkg.version, build, releases })) + '\n');
  const worker = path.join(dist, 'sw.js');
  await writeFile(worker, stampWorker(await readFile(worker, 'utf8'), build));
  console.log(`Release ${pkg.version} build ${build}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
