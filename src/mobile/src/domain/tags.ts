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

export function normalizeMemo(content: string, occurredAt: string) {
  const raw = content.trim();
  if (!raw) throw new Error('Il memo non può essere vuoto.');
  return {
    source: 'manual',
    externalId: 'local-' + occurredAt,
    contentType: 'text/plain',
    content: raw,
    tags: classifyMemo(raw),
    occurredAt
  };
}
