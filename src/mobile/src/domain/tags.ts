export type MemoryTag = 'lavoro' | 'relazioni' | 'attualita' | 'benessere' | 'personale';

export function classifyMemo(text: string): MemoryTag[] {
  const lower = text.toLocaleLowerCase('it');
  const tags: MemoryTag[] = [];
  if (/riunione|cliente|meeting|progetto|lavoro|work/.test(lower)) tags.push('lavoro');
  if (/amico|amicizia|famiglia|friend|family/.test(lower)) tags.push('relazioni');
  if (/attualit[aà]|news|notizi/.test(lower)) tags.push('attualita');
  if (/salute|palestra|health/.test(lower)) tags.push('benessere');
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
  if (!raw) throw new Error('Il memo non può essere vuoto.');
  let uuid: string | undefined;
  try {
    uuid = globalThis.crypto?.randomUUID?.();
  } catch {
    // Some non-Web demo runtimes expose crypto without implementing UUID.
    // Never silently downgrade the authenticated Web cloud path.
  }
  if (requireSecureId && !uuid)
    throw new Error('Identificativi sicuri non disponibili: apri Omnia con HTTPS e Web Crypto.');
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
