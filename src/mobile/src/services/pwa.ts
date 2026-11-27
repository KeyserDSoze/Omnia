// Versions, release notes and the update decision. Pure logic: the browser glue is in pwaClient.ts.
import type { Locale } from '../i18n';

export type Release = { version: string; date: string; notes: Record<string, string[]> };
export type VersionManifest = { version: string; build: string; builtAt: string; releases: Release[] };
export type UpdatePreference = 'ask' | 'silent';
export type UpdateDecision = 'none' | 'prompt' | 'silent';
export type ReleaseNotes = { version: string; date: string; notes: string[] };

/** Stamped at export time from package.json; the bundle itself carries no build number (see runningBuild). */
export const APP_VERSION = process.env.EXPO_PUBLIC_TAO_VERSION ?? '0.0.0-dev';

const semver = /^(\d{1,4})\.(\d{1,4})\.(\d{1,4})(-[0-9A-Za-z.-]{1,32})?$/;
const buildId = /^[a-f0-9]{6,64}$/;
const localeKey = /^[a-z]{2,3}(-[A-Za-z]{2,4})?$/;

/** Negative, zero or positive like a comparator; a pre-release sorts before its release. */
export function compareVersions(a: string, b: string): number {
  const x = semver.exec(a), y = semver.exec(b);
  if (!x || !y) return 0;
  for (let i = 1; i <= 3; i++) {
    const d = Number(x[i]) - Number(y[i]);
    if (d !== 0) return d;
  }
  return (x[4] ? 0 : 1) - (y[4] ? 0 : 1);
}

function parseRelease(raw: unknown): Release | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const r = raw as Record<string, unknown>;
  if (typeof r.version !== 'string' || !semver.test(r.version) ||
      typeof r.date !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(r.date) ||
      typeof r.notes !== 'object' || r.notes === null) return null;
  const notes: Record<string, string[]> = {};
  for (const [locale, lines] of Object.entries(r.notes as Record<string, unknown>)) {
    if (!localeKey.test(locale) || !Array.isArray(lines) || lines.length > 12 ||
        !lines.every(l => typeof l === 'string' && l.length > 0 && l.length <= 240)) return null;
    notes[locale] = lines as string[];
  }
  return { version: r.version, date: r.date, notes };
}

/** The document served as version.json, validated strictly: it is rendered, so it is never trusted as is. */
export function parseVersionManifest(raw: unknown): VersionManifest | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const m = raw as Record<string, unknown>;
  if (typeof m.version !== 'string' || !semver.test(m.version) ||
      typeof m.build !== 'string' || !buildId.test(m.build) ||
      typeof m.builtAt !== 'string' || Number.isNaN(Date.parse(m.builtAt)) ||
      !Array.isArray(m.releases) || m.releases.length > 20) return null;
  const releases = m.releases.map(parseRelease);
  if (releases.some(r => r === null)) return null;
  return { version: m.version, build: m.build, builtAt: m.builtAt, releases: releases as Release[] };
}

/** Releases newer than the installed one, newest first, in the user's language (English as the fallback). */
export function notesSince(manifest: VersionManifest, installedVersion: string, locale: Locale): ReleaseNotes[] {
  return manifest.releases
    .filter(r => compareVersions(r.version, installedVersion) > 0)
    .sort((a, b) => compareVersions(b.version, a.version))
    .map(r => ({ version: r.version, date: r.date, notes: r.notes[locale] ?? r.notes.en ?? [] }))
    .filter(r => r.notes.length > 0);
}

/** The notes of exactly one release, for the "what's new" line of the version that is running. */
export function notesOf(manifest: VersionManifest, version: string, locale: Locale): string[] {
  const release = manifest.releases.find(r => r.version === version);
  return release ? release.notes[locale] ?? release.notes.en ?? [] : [];
}

/**
 * A new build is out when its entry bundle differs from the one running. Asking is the default;
 * once the user has chosen "don't ask again" the update happens in the background; "later" hides
 * the prompt for that build until the next session.
 */
export function decideUpdate(input: {
  runningBuild: string | null; latest: VersionManifest; preference: UpdatePreference; dismissedBuild: string | null;
}): UpdateDecision {
  if (!input.runningBuild || input.runningBuild === input.latest.build) return 'none';
  if (input.preference === 'silent') return 'silent';
  return input.dismissedBuild === input.latest.build ? 'none' : 'prompt';
}

const PREFERENCE_KEY = 'tao.update.preference';
const DISMISSED_KEY = 'tao.update.dismissed';

export function loadPreference(storage: Pick<Storage, 'getItem'> | null): UpdatePreference {
  try { return storage?.getItem(PREFERENCE_KEY) === 'silent' ? 'silent' : 'ask'; } catch { return 'ask'; }
}

export function savePreference(storage: Pick<Storage, 'setItem'> | null, value: UpdatePreference): void {
  try { storage?.setItem(PREFERENCE_KEY, value); } catch { /* private mode: the choice lasts until the page closes */ }
}

export function loadDismissed(storage: Pick<Storage, 'getItem'> | null): string | null {
  try {
    const value = storage?.getItem(DISMISSED_KEY) ?? null;
    return value !== null && buildId.test(value) ? value : null;
  } catch { return null; }
}

export function saveDismissed(storage: Pick<Storage, 'setItem'> | null, build: string): void {
  try { storage?.setItem(DISMISSED_KEY, build); } catch { /* ignored */ }
}

/** The entry bundle hash of the page that is running, read from its own script tag. */
export function runningBuild(scripts: readonly string[]): string | null {
  for (const src of scripts) {
    const match = /\/_expo\/static\/js\/web\/entry-([a-f0-9]{6,64})\.js(?:$|\?)/.exec(src);
    if (match) return match[1]!;
  }
  return null;
}
