// One-off generator for the brand images in public/ (run: node scripts/make-icons.mjs).
// Renders the Tao symbol with Playwright's Chromium, so no image library is needed. The PNG and ICO
// files it writes are committed; run it again only when the symbol or the tagline changes.
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '@playwright/test';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.join(root, 'public');
const INK = '#06070B';
const YIN = '#151A29';
const YANG = '#F4F1E8';

/** The symbol of the app (src/components/TaoAvatar.tsx) in a -110..110 box. */
const symbol = `<g transform="rotate(180)"><circle r="100" fill="${YANG}"/><path d="M0,-100A100,100 0 0 0 0,100Z" fill="${YIN}"/>` +
  `<circle cy="-50" r="50" fill="${YANG}"/><circle cy="50" r="50" fill="${YIN}"/><circle cy="-50" r="13" fill="${YIN}"/>` +
  `<circle cy="50" r="13" fill="${YANG}"/><circle r="99.5" fill="none" stroke="#fff" stroke-opacity=".35"/></g>`;

const iconSvg = (size, scale) => `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">` +
  `<rect width="${size}" height="${size}" rx="${size * 0.22}" fill="${INK}"/>` +
  `<g transform="translate(${size / 2} ${size / 2}) scale(${(size * scale) / 200})">${symbol}</g></svg>`;

const ogHtml = `<!doctype html><meta charset="utf-8"><style>
html,body{margin:0;width:1200px;height:630px;background:${INK};color:#EDEFF5;overflow:hidden;
font-family:"Iowan Old Style","Palatino Linotype",Palatino,"Book Antiqua",Georgia,serif}
.aura{position:absolute;inset:0;background:radial-gradient(60% 70% at 25% 0%,rgba(72,214,183,.40),transparent 70%),
radial-gradient(55% 60% at 95% 100%,rgba(124,110,255,.30),transparent 70%)}
.row{position:relative;display:flex;align-items:center;gap:72px;height:630px;padding:0 100px}
h1{font-weight:400;font-size:132px;letter-spacing:-.02em;margin:0 0 8px}
p{margin:0;font:400 38px/1.35 -apple-system,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;color:#B9BFD0;max-width:620px}
svg{filter:drop-shadow(0 0 40px rgba(72,214,183,.45))}</style>
<div class="aura"></div><div class="row"><svg width="300" height="300" viewBox="-110 -110 220 220">${symbol}</svg>
<div><h1>tao</h1><p>Your working day, sorted: one card at a time.</p></div></div>`;

/** An ICO that embeds PNG images (supported by every current browser). */
function ico(images) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);
  let offset = 6 + images.length * 16;
  const entries = images.map(({ size, png }) => {
    const entry = Buffer.alloc(16);
    entry.writeUInt8(size >= 256 ? 0 : size, 0);
    entry.writeUInt8(size >= 256 ? 0 : size, 1);
    entry.writeUInt16LE(1, 4);
    entry.writeUInt16LE(32, 6);
    entry.writeUInt32LE(png.length, 8);
    entry.writeUInt32LE(offset, 12);
    offset += png.length;
    return entry;
  });
  return Buffer.concat([header, ...entries, ...images.map(i => i.png)]);
}

await mkdir(out, { recursive: true });
await writeFile(path.join(out, 'favicon.svg'), iconSvg(64, 0.8) + '\n');

const browser = await chromium.launch();
try {
  const page = await browser.newPage();
  const png = async (svg, size) => {
    await page.setViewportSize({ width: size, height: size });
    await page.setContent(`<style>html,body{margin:0;background:transparent}</style>${svg}`);
    return page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
  };
  // Maskable-safe: the symbol stays inside the central 60 %.
  await writeFile(path.join(out, 'apple-touch-icon.png'), await png(iconSvg(180, 0.6), 180));
  await writeFile(path.join(out, 'icon-192.png'), await png(iconSvg(192, 0.6), 192));
  await writeFile(path.join(out, 'icon-512.png'), await png(iconSvg(512, 0.6), 512));
  const small = [{ size: 16, png: await png(iconSvg(16, 0.86), 16) }, { size: 32, png: await png(iconSvg(32, 0.86), 32) },
    { size: 48, png: await png(iconSvg(48, 0.84), 48) }];
  await writeFile(path.join(out, 'favicon.ico'), ico(small));
  await page.setViewportSize({ width: 1200, height: 630 });
  await page.setContent(ogHtml);
  await writeFile(path.join(out, 'og-image.png'), await page.screenshot({ clip: { x: 0, y: 0, width: 1200, height: 630 } }));
} finally {
  await browser.close();
}
console.log('written to', out);
