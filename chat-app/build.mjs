// Bundles main, preload and renderer into dist/, which is all the app ships.
import { build } from 'esbuild';
import { cpSync, mkdirSync, rmSync } from 'node:fs';

rmSync('dist', { recursive: true, force: true });
mkdirSync('dist/renderer', { recursive: true });

const shared = { bundle: true, minify: true, sourcemap: false, logLevel: 'warning' };

await build({
  ...shared,
  entryPoints: ['src/main/main.ts'],
  outfile: 'dist/main.cjs',
  platform: 'node',
  format: 'cjs',
  external: ['electron'],
});
await build({
  ...shared,
  entryPoints: ['src/preload.ts'],
  outfile: 'dist/preload.cjs',
  platform: 'node',
  format: 'cjs',
  external: ['electron'],
});
await build({
  ...shared,
  entryPoints: ['src/renderer/app.ts'],
  outfile: 'dist/renderer/app.js',
  platform: 'browser',
  format: 'iife',
});

cpSync('src/renderer/index.html', 'dist/renderer/index.html');
cpSync('src/renderer/styles.css', 'dist/renderer/styles.css');
cpSync('build/icon.png', 'dist/icon.png');
