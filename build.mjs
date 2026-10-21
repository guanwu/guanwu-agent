// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

import { build } from "esbuild";
import { execFileSync } from "node:child_process";
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const dist = join(root, "docs", "dist");
const entry = join(root, "src", "GuanwuAgentSdk", "index.ts");
const tsc = join(root, "node_modules", "typescript", "bin", "tsc");

rmSync(dist, { recursive: true, force: true });
mkdirSync(dist, { recursive: true });

const shared = {
  entryPoints: [entry],
  bundle: true,
  platform: "browser",
  target: ["es2020"],
  legalComments: "none",
  sourcemap: true,
  logLevel: "info",
};

await build({
  ...shared,
  format: "iife",
  globalName: "GuanwuAgent",
  outfile: join(dist, "guanwu-agent.iife.js"),
});

await build({
  ...shared,
  format: "iife",
  globalName: "GuanwuAgent",
  minify: true,
  outfile: join(dist, "guanwu-agent.iife.min.js"),
});

await build({
  ...shared,
  format: "esm",
  outfile: join(dist, "guanwu-agent.esm.js"),
});

await build({
  ...shared,
  format: "esm",
  minify: true,
  outfile: join(dist, "guanwu-agent.esm.min.js"),
});

execFileSync(process.execPath, [tsc, "-p", join(root, "tsconfig.build.json")], { stdio: "inherit" });

writeFileSync(
  join(dist, "package.json"),
  `${JSON.stringify(
    {
      type: "module",
      sideEffects: false,
      author: "Jiaowei <wei.jiao@guanwu.com>",
      license: "AGPL-3.0-only",
    },
    null,
    2,
  )}\n`,
);

console.log("build done: docs/dist/guanwu-agent.[iife|esm][.min].js (+ .map) + docs/dist/*.d.ts");
