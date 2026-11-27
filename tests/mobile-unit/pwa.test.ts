import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  compareVersions, decideUpdate, loadDismissed, loadPreference, notesOf, notesSince, parseVersionManifest,
  runningBuild, saveDismissed, savePreference, type VersionManifest
} from '../../src/mobile/src/services/pwa';
import { buildIdOf, stampWorker, versionManifest } from '../../src/mobile/scripts/release.mjs';

const notes = JSON.parse(readFileSync(new URL('../../src/mobile/release-notes.json', import.meta.url), 'utf8'));
const pkg = JSON.parse(readFileSync(new URL('../../src/mobile/package.json', import.meta.url), 'utf8'));
const manifest = (over: Partial<VersionManifest> = {}): VersionManifest => ({
  version: '0.3.0', build: 'abcdef12', builtAt: '2026-11-27T10:00:00Z',
  releases: [
    { version: '0.3.0', date: '2026-11-28', notes: { en: ['Reminders.'], it: ['Promemoria.'] } },
    { version: '0.2.0', date: '2026-11-27', notes: { en: ['Install as an app.'], it: ['Installa come app.'] } },
    { version: '0.1.0', date: '2026-11-01', notes: { en: ['First release.'] } }
  ], ...over
});

describe('versions', () => {
  it('compares numerically and puts a pre-release before its release', () => {
    expect(compareVersions('0.10.0', '0.9.0')).toBeGreaterThan(0);
    expect(compareVersions('1.0.0', '1.0.0')).toBe(0);
    expect(compareVersions('1.0.0-rc.1', '1.0.0')).toBeLessThan(0);
    expect(compareVersions('nonsense', '1.0.0')).toBe(0);
  });

  it('keeps the release notes in step with the app version', () => {
    expect(notes[0].version).toBe(pkg.version);
    expect(notes.every((r: { notes: { en: string[]; it: string[] } }) => r.notes.en.length > 0 && r.notes.it.length > 0)).toBe(true);
  });
});

describe('the version manifest', () => {
  it('accepts what the build produces', () => {
    const built = versionManifest({ version: pkg.version, build: 'abcdef12', releases: notes });
    expect(parseVersionManifest(JSON.parse(JSON.stringify(built)))).not.toBeNull();
  });

  it.each([
    null, 'text', {}, { ...manifest(), version: '1' }, { ...manifest(), build: 'NOT-HEX' },
    { ...manifest(), builtAt: 'yesterday' }, { ...manifest(), releases: 'x' },
    { ...manifest(), releases: [{ version: '1.0.0', date: '2026-01-01', notes: { en: [''] } }] },
    { ...manifest(), releases: [{ version: '1.0.0', date: '2026-01-01', notes: { en: [1] } }] },
    { ...manifest(), releases: [{ version: '1.0.0', date: '2026-01-01', notes: { 'not a locale': ['x'] } }] },
    { ...manifest(), releases: [{ version: '1.0.0', date: '2026-01-01', notes: { en: ['x'.repeat(241)] } }] }
  ])('refuses a malformed document: %j', raw => expect(parseVersionManifest(raw)).toBeNull());

  it('lists the notes of the releases newer than the installed one in the user language', () => {
    const since = notesSince(manifest(), '0.1.0', 'it');
    expect(since.map(r => r.version)).toEqual(['0.3.0', '0.2.0']);
    expect(since[0]!.notes).toEqual(['Promemoria.']);
    expect(notesSince(manifest(), '0.3.0', 'it')).toEqual([]);
    // No Italian text for 0.1.0: English is the fallback.
    expect(notesSince(manifest(), '0.0.1', 'it').at(-1)!.notes).toEqual(['First release.']);
    expect(notesOf(manifest(), '0.2.0', 'it')).toEqual(['Installa come app.']);
    expect(notesOf(manifest(), '9.9.9', 'it')).toEqual([]);
  });
});

describe('the update decision', () => {
  const latest = manifest();
  it('does nothing when the running build is current or unknown', () => {
    expect(decideUpdate({ runningBuild: 'abcdef12', latest, preference: 'ask', dismissedBuild: null })).toBe('none');
    expect(decideUpdate({ runningBuild: null, latest, preference: 'ask', dismissedBuild: null })).toBe('none');
  });

  it('asks by default, and stays quiet for a build the user postponed', () => {
    expect(decideUpdate({ runningBuild: '11111111', latest, preference: 'ask', dismissedBuild: null })).toBe('prompt');
    expect(decideUpdate({ runningBuild: '11111111', latest, preference: 'ask', dismissedBuild: 'abcdef12' })).toBe('none');
    // A newer build than the postponed one asks again.
    expect(decideUpdate({ runningBuild: '11111111', latest, preference: 'ask', dismissedBuild: '99999999' })).toBe('prompt');
  });

  it('updates in the background once the user chose not to be asked again', () => {
    expect(decideUpdate({ runningBuild: '11111111', latest, preference: 'silent', dismissedBuild: null })).toBe('silent');
  });
});

describe('stored choices', () => {
  const store = () => {
    const data = new Map<string, string>();
    return { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => void data.set(k, v) };
  };

  it('round-trips the preference and the postponed build', () => {
    const s = store();
    expect(loadPreference(s)).toBe('ask');
    savePreference(s, 'silent');
    expect(loadPreference(s)).toBe('silent');
    expect(loadDismissed(s)).toBeNull();
    saveDismissed(s, 'abcdef12');
    expect(loadDismissed(s)).toBe('abcdef12');
  });

  it('survives a browser that refuses storage and ignores junk', () => {
    const broken = { getItem: () => { throw new Error('denied'); }, setItem: () => { throw new Error('denied'); } };
    expect(loadPreference(broken)).toBe('ask');
    expect(loadDismissed(broken)).toBeNull();
    expect(() => { savePreference(broken, 'silent'); saveDismissed(broken, 'abcdef12'); }).not.toThrow();
    expect(loadDismissed({ getItem: () => '<script>' })).toBeNull();
    expect(loadPreference(null)).toBe('ask');
  });
});

describe('the build that is running', () => {
  it('is the entry bundle hash found in the page scripts', () => {
    expect(runningBuild(['https://taoai.me/_expo/static/js/web/entry-4eab825b949dedd4cce1405a0154a22c.js']))
      .toBe('4eab825b949dedd4cce1405a0154a22c');
    expect(runningBuild(['https://x.test/other.js', 'http://127.0.0.1:19006/Tao/_expo/static/js/web/entry-abc123def.js?v=1']))
      .toBe('abc123def');
    expect(runningBuild(['http://localhost:8081/index.bundle?platform=web'])).toBeNull();
    expect(runningBuild([])).toBeNull();
  });
});

describe('the release step of the build', () => {
  it('finds the entry bundle and writes the newest release first', () => {
    expect(buildIdOf(['index.html', 'entry-4eab825b.js', 'other-1.js'])).toBe('4eab825b');
    expect(buildIdOf(['index.html'])).toBeNull();
    const built = versionManifest({ version: pkg.version, build: '4eab825b', releases: [...notes].reverse(), now: new Date('2026-11-27T10:00:00Z') });
    expect(built.releases[0].version).toBe(pkg.version);
    expect(built.builtAt).toBe('2026-11-27T10:00:00.000Z');
  });

  it('refuses release notes that do not start with the app version', () => {
    expect(() => versionManifest({ version: '9.9.9', build: '4eab825b', releases: notes })).toThrow(/must start with/);
  });

  it('stamps the build into the worker so that a new bundle means a new worker', () => {
    const source = readFileSync(new URL('../../src/mobile/public/sw.js', import.meta.url), 'utf8');
    expect(stampWorker(source, '4eab825b')).toContain("const BUILD = '4eab825b';");
    expect(() => stampWorker('no marker', '4eab825b')).toThrow();
  });
});
