import { getLocale, t, translate, type Locale, type MessageKey } from '../i18n';

export type MemoryTag = 'lavoro' | 'relazioni' | 'attualita' | 'benessere' | 'personale';

const tagWords: [MemoryTag, MessageKey][] = [
  ['lavoro', 'tagwords.work'], ['relazioni', 'tagwords.relations'],
  ['attualita', 'tagwords.news'], ['benessere', 'tagwords.wellbeing']
];

/** Keyword rules, not AI: the words of the current language, plus Italian and English. */
export function classifyMemo(text: string, locale: Locale = getLocale()): MemoryTag[] {
  const lower = text.toLocaleLowerCase();
  const languages = [...new Set<Locale>([locale, 'en', 'it'])];
  const tags = tagWords.filter(([, key]) => languages.some(language =>
    translate(language, key).split('|').some(word => word.trim() !== '' && lower.includes(word.trim().toLocaleLowerCase())))
  ).map(([tag]) => tag);
  return tags.length ? tags : ['personale'];
}

export type ReturnTypeMemo = ReturnType<typeof normalizeMemo>;

// Native currently has only an in-memory demo. This counter prevents duplicate
// React list keys within that demo, but is NOT a cloud-safe source identifier.
let demoCaptureSequence = 0;

/**
 * Each independent capture receives a new source key, independent of its
 * occurredAt. The caller retains the returned entry for idempotent retries.
 * Web cloud requires a cryptographically random ID (secure-context Web Crypto).
 */
export function normalizeMemo(content: string, occurredAt: string, requireSecureId = false) {
  const raw = content.trim();
  if (!raw) throw new Error(t('notice.emptyMemo'));
  let uuid: string | undefined;
  try {
    uuid = globalThis.crypto?.randomUUID?.();
  } catch {
    // Some non-Web demo runtimes expose crypto without implementing UUID.
    // Never silently downgrade the authenticated Web cloud path.
  }
  if (requireSecureId && !uuid)
    throw new Error(t('notice.noSecureIds'));
  const externalId = uuid ? 'local-' + uuid :
    'demo-' + occurredAt + '-' + (++demoCaptureSequence).toString(36);
  return {
    source: 'manual',
    externalId,
    contentType: 'text/plain',
    content: raw,
    tags: classifyMemo(raw),
    occurredAt
  };
}
