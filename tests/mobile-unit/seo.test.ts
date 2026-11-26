import { describe, expect, it } from 'vitest';
import { llmsTxt, robotsTxt, siteUrl, sitemapXml } from '../../src/mobile/scripts/seo.mjs';

describe('site address', () => {
  it('accepts a plain https address and drops the trailing slash', () => {
    expect(siteUrl('https://taoai.me')).toBe('https://taoai.me');
    expect(siteUrl(' https://taoai.me/ ')).toBe('https://taoai.me');
    expect(siteUrl('https://example.com/Tao/')).toBe('https://example.com/Tao');
  });

  it.each([undefined, '', '   ', 'http://taoai.me', 'taoai.me', 'https://u:p@taoai.me', 'https://taoai.me?x=1', 'https://taoai.me#a'])(
    'refuses %s', value => expect(siteUrl(value as string | undefined)).toBeNull());
});

describe('crawler files', () => {
  it('closes a private install completely', () => {
    expect(robotsTxt(null)).toBe('User-agent: *\nDisallow: /\n');
  });

  it('opens the public site, keeps the OAuth callback out and points to the sitemap', () => {
    const robots = robotsTxt('https://taoai.me');
    expect(robots).toContain('Allow: /');
    expect(robots).toContain('Disallow: /connections/');
    expect(robots).toContain('Sitemap: https://taoai.me/sitemap.xml');
  });

  it('lists the home page with the build date', () => {
    const xml = sitemapXml('https://taoai.me', new Date('2026-11-26T10:00:00Z'));
    expect(xml).toContain('<loc>https://taoai.me/</loc>');
    expect(xml).toContain('<lastmod>2026-11-26</lastmod>');
  });

  it('describes Tao for AI assistants without promising what is not built', () => {
    const text = llmsTxt('https://taoai.me');
    expect(text.startsWith('# Tao\n\n> ')).toBe(true);
    expect(text).toContain('[Open Tao](https://taoai.me/)');
    expect(text).toContain('Tao never sends messages on its own');
    expect(text).toContain('More are planned: WhatsApp, Telegram, MCP servers and direct APIs');
    expect(text).toContain('## Italiano');
  });
});
