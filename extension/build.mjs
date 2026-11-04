// Bundles src/ into dist/, which Chrome loads with "Load unpacked".
// Usage: node build.mjs [--watch]

import { cpSync, mkdirSync, rmSync } from "node:fs";
import * as esbuild from "esbuild";

const watch = process.argv.includes("--watch");

rmSync("dist", { recursive: true, force: true });
mkdirSync("dist", { recursive: true });
cpSync("manifest.json", "dist/manifest.json");
cpSync("src/options/options.html", "dist/options.html");

const options = {
  entryPoints: {
    background: "src/background.ts",
    // One bundle per chat site: core + that site's adapter only.
    "content-whatsapp": "src/content/entry-whatsapp.ts",
    options: "src/options/options.ts",
  },
  outdir: "dist",
  bundle: true,
  // Content scripts can't be ES modules, so everything is an IIFE.
  format: "iife",
  target: "chrome120",
  minify: !watch,
  sourcemap: watch ? "inline" : false,
  logLevel: "info",
};

if (watch) {
  const ctx = await esbuild.context(options);
  await ctx.watch();
} else {
  await esbuild.build(options);
}
