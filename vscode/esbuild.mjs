// The extension is bundled into one file. The unit tests are bundled too, so they can
// import the shared types from ../frontend/src without a tsc rootDir.
import { build, context } from "esbuild";
import { readdirSync, existsSync, rmSync } from "node:fs";
import { fileURLToPath } from "node:url";

const watch = process.argv.includes("--watch");
const tests = process.argv.includes("--tests");

const common = { bundle: true, platform: "node", target: "node20", format: "cjs", sourcemap: true, logLevel: "info" };

// The notebook renderer runs in the webview and draws outputs with the browser editor's React
// components. React and vega come from the frontend's node_modules. vega is loaded only when a
// chart is shown, as its own chunk next to index.js.
const renderer = {
  entryPoints: ["src/renderer/index.tsx"],
  outdir: "out/renderer",
  bundle: true,
  platform: "browser",
  format: "esm",
  splitting: true,
  target: "es2022",
  minify: true,
  sourcemap: true,
  logLevel: "info",
  jsx: "automatic",
  loader: { ".css": "text" },
  nodePaths: [fileURLToPath(new URL("../frontend/node_modules", import.meta.url))],
  define: { "process.env.NODE_ENV": JSON.stringify("production") },
};

const jobs = tests
  ? ["unit", "integration"]
      .filter((d) => existsSync(`test/${d}`))
      .map((d) => ({
        ...common,
        entryPoints: readdirSync(`test/${d}`)
          .filter((f) => f.endsWith(".ts"))
          .map((f) => `test/${d}/${f}`),
        outdir: `out/test/${d}`,
        external: ["vscode", "mocha"],
      }))
  : [{ ...common, entryPoints: ["src/extension.ts"], outfile: "out/extension.js", external: ["vscode"] }, renderer];

// Chunk names change with their contents; clear old ones so they don't end up in the .vsix.
if (!tests) rmSync(renderer.outdir, { recursive: true, force: true });

for (const job of jobs) {
  if (watch) await (await context(job)).watch();
  else await build(job);
}
