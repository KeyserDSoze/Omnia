// Search and AI-answer files for the exported web app (run after `expo export`; see "build:web").
// With TAO_SITE_URL (the public address, e.g. https://taoai.me) the site is open to crawlers and
// described for AI assistants; without it (a private on-prem install) everything is closed.
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** The public address without a trailing slash, or null when it is unset or not a plain https address. */
export function siteUrl(raw) {
  if (!raw || !raw.trim()) return null;
  try {
    const url = new URL(raw.trim());
    if (url.protocol !== 'https:' || url.search || url.hash || url.username) return null;
    return (url.origin + url.pathname).replace(/\/+$/, '');
  } catch {
    return null;
  }
}

export function robotsTxt(site) {
  if (!site) return 'User-agent: *\nDisallow: /\n';
  return `User-agent: *\nAllow: /\nDisallow: /connections/\n\nSitemap: ${site}/sitemap.xml\n`;
}

export function sitemapXml(site, now = new Date()) {
  return '<?xml version="1.0" encoding="UTF-8"?>\n' +
    '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n' +
    `  <url><loc>${site}/</loc><lastmod>${now.toISOString().slice(0, 10)}</lastmod></url>\n</urlset>\n`;
}

/** llms.txt (llmstxt.org): a short, factual description that AI assistants can quote. */
export function llmsTxt(site) {
  return `# Tao

> Tao is a personal assistant that sorts your working day. It reads your calendar, mail and tasks from Microsoft 365 and Google Workspace when you ask, turns them into cards you mark done, not done, later or dismissed, and can draft a plan for the day with AI only after you give a dedicated consent.

## What Tao does
- Daily deck: today's meetings, unread or important mail, open tasks and unread Teams chats become cards, one at a time.
- Chat first: you talk or type to Tao; the chat answers deterministically, and AI is used only for the optional day plan.
- Connections: Microsoft 365 (Outlook, Calendar, To Do, Teams) and Google Workspace (Gmail, Calendar, Tasks). More are planned: WhatsApp, Telegram, MCP servers and direct APIs.
- Languages: 21, including Italian, English, Spanish, French, German, Portuguese, Chinese, Japanese, Arabic and Hindi.

## Privacy and control
- Tao reads on demand and does not copy mailboxes or calendars. Writes (completing a task) happen only after you confirm that single action.
- Tao never sends messages on its own.
- Notes and credentials are encrypted at rest; you can export and erase your data.
- AI features need a separate consent and receive minimised data.

## Links
- [Open Tao](${site}/): the web app.
- [Source code and documentation](https://github.com/KeyserDSoze/Tao): architecture decisions, privacy design and roadmap.

## Italiano
Tao è un assistente personale che smista la tua giornata di lavoro: calendario, posta e attività di Microsoft 365 e Google Workspace diventano carte da segnare fatte, non fatte, dopo o scartate. Legge solo quando lo chiedi, scrive solo con la tua conferma e usa l'AI solo con un consenso dedicato.
`;
}

async function main() {
  const dist = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'dist');
  const site = siteUrl([process.env.TAO_SITE_URL, process.env.TAO_WEB_BASE_PATH].filter(Boolean).join(''));
  await mkdir(dist, { recursive: true });
  await writeFile(path.join(dist, 'robots.txt'), robotsTxt(site));
  if (site) {
    await writeFile(path.join(dist, 'sitemap.xml'), sitemapXml(site));
    await writeFile(path.join(dist, 'llms.txt'), llmsTxt(site));
  }
  console.log(site ? `SEO files for ${site}` : 'SEO files: private install, closed to crawlers');
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
