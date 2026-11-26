import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { LOCALES, translate, type Locale } from '../../src/mobile/src/i18n';
import { defaultIntegrations, integrationLines, intentOf } from '../../src/mobile/src/services/chatScript';

const asking: Record<Locale, string> = {
  it: 'cosa posso allacciare?', en: 'what can I connect?', zh: '可以连接什么', hi: 'क्या जोड़ सकता हूँ',
  es: '¿qué puedo conectar?', fr: 'que puis-je connecter ?', ar: 'ماذا يمكنني ربطه', bn: 'কী সংযোগ করতে পারি',
  pt: 'quais integrações existem?', ru: 'что можно подключить', ur: 'کیا جوڑ سکتا ہوں', id: 'apa yang bisa dihubungkan',
  de: 'was kann ich verbinden?', ja: '何を接続できますか', ko: '무엇을 연결할 수 있어?', tr: 'neleri bağlayabilirim',
  vi: 'có thể kết nối gì', fa: 'چه چیزی می‌توانم وصل کنم', pl: 'co mogę podłączyć', nl: 'wat kan ik koppelen',
  th: 'เชื่อมต่ออะไรได้'
};

describe('“what can I connect?” in the chat', () => {
  it.each(LOCALES.map(l => [l.code]))('%s understands the question', code => {
    expect(intentOf(asking[code as Locale], code as Locale)).toBe('integrations');
  });

  it('understands the English words in any language', () => {
    expect(intentOf('show me the integrations', 'de')).toBe('integrations');
    expect(intentOf('can I add an MCP server?', 'ja')).toBe('integrations');
    expect(intentOf('what about WhatsApp?', 'fr')).toBe('integrations');
  });

  it('keeps asking to connect an account as the guided setup', () => {
    expect(intentOf('voglio collegare un altro account', 'it')).toBe('setup');
    expect(intentOf('connect my account', 'en')).toBe('setup');
    expect(intentOf('conectar minha conta', 'pt')).toBe('setup');
    expect(intentOf('cosa devo fare oggi', 'it')).toBe('today');
  });

  it('answers with the platform integrations, then MCP servers, then direct APIs', () => {
    const lines = integrationLines(defaultIntegrations, 'it');
    expect(lines).toHaveLength(4);
    expect(lines[0]).toBe(translate('it', 'chat.integrationsIntro'));
    expect(lines[1]).toContain(translate('it', 'integrations.builtIn'));
    expect(lines[1]).toContain('Microsoft 365');
    expect(lines[1]).toContain('Google Workspace');
    expect(lines[1]).toContain('WhatsApp');
    expect(lines[2]).toBe(translate('it', 'integrations.mcp'));
    expect(lines[3]).toBe(translate('it', 'integrations.api'));
  });

  it('marks what is already available and what is coming, in the user’s language', () => {
    const [, builtIn] = integrationLines(defaultIntegrations, 'en');
    const row = (name: string) => builtIn.split('\n').find(l => l.includes(name))!;
    expect(row('Microsoft 365')).toContain(translate('en', 'integrations.available'));
    expect(row('Google Workspace')).toContain(translate('en', 'integrations.available'));
    expect(row('Telegram')).toContain(translate('en', 'integrations.planned'));
    expect(row('WhatsApp Business')).toContain(translate('en', 'integrations.planned'));
  });

  it('shows the server catalog as sent, and names unknown entries by their id', () => {
    const lines = integrationLines([
      { id: 'microsoft-365', kind: 'built_in', direction: 'source', auth: 'oauth', status: 'available', capabilities: [], onPremOnly: false },
      { id: 'signal', kind: 'built_in', direction: 'channel', auth: 'pairing', status: 'planned', capabilities: [], onPremOnly: false },
      { id: 'mcp-open', kind: 'mcp', direction: 'tool', auth: 'none', status: 'planned', capabilities: [], onPremOnly: false }
    ], 'en');
    expect(lines[1]).toContain('signal');
    expect(lines[1]).not.toContain('Google Workspace');
  });

  it('keeps the offline list in step with the server catalog', () => {
    const source = readFileSync(new URL('../../src/backend/Tao.Domain/IntegrationCatalog.cs', import.meta.url), 'utf8');
    const serverBuiltIn = [...source.matchAll(/new\("([a-z0-9-]+)", IntegrationKind\.BuiltIn/g)].map(m => m[1]);
    expect(defaultIntegrations.map(e => e.id)).toEqual(serverBuiltIn);
  });
});
