// The extension is bundled into one file. The unit tests are bundled too, so they can
// import the shared types from ../frontend/src without a tsc rootDir.
import { build, context } from "esbuild";
import { readdirSync, existsSync } from "node:fs";

const watch = process.argv.includes("--watch");
const tests = process.argv.includes("--tests");

const common = { bundle: true, platform: "node", target: "node20", format: "cjs", sourcemap: true, logLevel: "info" };

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
  : [{ ...common, entryPoints: ["src/extension.ts"], outfile: "out/extension.js", external: ["vscode"] }];

for (const job of jobs) {
  if (watch) await (await context(job)).watch();
  else await build(job);
}
