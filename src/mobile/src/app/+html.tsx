import { ScrollViewStyleReset, useServerDocumentContext } from 'expo-router/html';
import type { PropsWithChildren } from 'react';

// Rendered once at export time. TAO_SITE_URL is the public address (https://taoai.me); without it
// (a private on-prem install) the page asks crawlers to stay away and carries no absolute addresses.
const base = (process.env.EXPO_BASE_URL ?? process.env.TAO_WEB_BASE_PATH ?? '').replace(/\/+$/, '');
const origin = (process.env.TAO_SITE_URL ?? '').trim().replace(/\/+$/, '');
const site = /^https:\/\/[^/\s?#@]+$/.test(origin) ? origin + base : null;

const title = 'Tao: your working day, sorted';
const description = 'Tao is a personal assistant that sorts your working day: Outlook and Google calendar, mail and tasks become cards you mark done, later or dismissed.';

const structuredData = site && {
  '@context': 'https://schema.org',
  '@type': 'WebApplication',
  name: 'Tao',
  url: `${site}/`,
  description,
  applicationCategory: 'ProductivityApplication',
  operatingSystem: 'Any (web browser)',
  image: `${site}/og-image.png`,
  inLanguage: ['it', 'en', 'es', 'fr', 'de', 'pt', 'zh', 'ja', 'ko', 'ar', 'hi', 'ru'],
  featureList: [
    'Daily deck of meetings, mail, tasks and chats as cards',
    'Microsoft 365 and Google Workspace connections, read-only by default',
    'Optional AI day plan with a dedicated consent',
    'Encrypted notes with data export and erasure'
  ]
};

export default function Root({ children }: PropsWithChildren) {
  // The exporter hands over the bundle, style and hydration tags here; dropping them breaks the app.
  const { bodyAttributes, bodyNodes, htmlAttributes, headNodes } = useServerDocumentContext();
  return (
    <html lang="en" {...htmlAttributes}>
      <head>
        <meta charSet="utf-8" />
        <meta httpEquiv="X-UA-Compatible" content="IE=edge" />
        <meta name="viewport" content="width=device-width, initial-scale=1, shrink-to-fit=no" />
        <title>{title}</title>
        <meta name="description" content={description} />
        <meta name="robots" content={site ? 'index, follow, max-image-preview:large' : 'noindex, nofollow'} />
        <meta name="theme-color" content="#06070B" />
        <link rel="icon" href={`${base}/favicon.ico`} sizes="48x48" />
        <link rel="icon" href={`${base}/favicon.svg`} type="image/svg+xml" sizes="any" />
        <link rel="apple-touch-icon" href={`${base}/apple-touch-icon.png`} />
        <link rel="manifest" href={`${base}/site.webmanifest`} />
        {site && (
          <>
            <link rel="canonical" href={`${site}/`} />
            <meta property="og:type" content="website" />
            <meta property="og:site_name" content="Tao" />
            <meta property="og:title" content={title} />
            <meta property="og:description" content={description} />
            <meta property="og:url" content={`${site}/`} />
            <meta property="og:image" content={`${site}/og-image.png`} />
            <meta property="og:image:width" content="1200" />
            <meta property="og:image:height" content="630" />
            <meta property="og:image:alt" content="The Tao symbol and the words: your working day, sorted" />
            <meta name="twitter:card" content="summary_large_image" />
            <meta name="twitter:title" content={title} />
            <meta name="twitter:description" content={description} />
            <meta name="twitter:image" content={`${site}/og-image.png`} />
            <link rel="alternate" type="text/plain" href={`${site}/llms.txt`} title="Tao for AI assistants" />
            {/* A data block, not executable: the Content-Security-Policy does not apply to it. */}
            <script type="application/ld+json"
              dangerouslySetInnerHTML={{ __html: JSON.stringify(structuredData).replace(/</g, '\\u003c') }} />
          </>
        )}
        <ScrollViewStyleReset />
        {headNodes}
      </head>
      <body {...bodyAttributes}>
        {children}
        {bodyNodes}
        <noscript>
          <h1>Tao: your working day, sorted</h1>
          <p>{description}</p>
          <p>Tao needs JavaScript to run. It reads your calendar, mail and tasks only when you ask, writes only after you
            confirm, and uses AI only with a dedicated consent.</p>
          <p><a href="https://github.com/KeyserDSoze/Tao">Source code and documentation</a></p>
        </noscript>
      </body>
    </html>
  );
}
