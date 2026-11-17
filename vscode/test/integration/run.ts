// Starts VS Code with the extension and runs the integration suites (see index.ts).
// Needs the CLI built from this checkout: run `dotnet build Reverb.slnx` first. Set
// REVERB_CONFIGURATION=Release to use the Release build (default: Debug).
//
//   npm run test:integration                     every suite
//   npm run test:integration -- --grep serializer
//
// VS Code starts twice: once trusted for *.test.ts, once in Restricted Mode for
// *.restricted.test.ts. Each start gets a fresh copy of tests/fixtures as its workspace and a fresh
// user data folder. REVERB_VSCODE_VERSION picks the VS Code version (default: stable).
import { spawn } from "node:child_process";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { downloadAndUnzipVSCode, runTests } from "@vscode/test-electron";

const extensionRoot = path.resolve(__dirname, "../../..");
const repoRoot = path.resolve(extensionRoot, "..");
const cachePath = path.join(extensionRoot, ".vscode-test");

async function main() {
  const grepAt = process.argv.indexOf("--grep");
  const grep = grepAt >= 0 ? (process.argv[grepAt + 1] ?? "") : "";

  // When this runs from a VS Code terminal these would make the test instance misbehave.
  delete process.env.ELECTRON_RUN_AS_NODE;
  for (const k of Object.keys(process.env)) if (k.startsWith("VSCODE_")) delete process.env[k];

  const reverb = reverbCommand();
  const vscodeExecutablePath = await downloadAndUnzipVSCode({
    cachePath,
    version: process.env.REVERB_VSCODE_VERSION || "stable",
  });
  // VS Code puts a socket in the user data folder, and macOS limits socket paths to 104 bytes,
  // so these names are short.
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "rvb-"));
  try {
    for (const restricted of [false, true]) {
      const dir = path.join(temp, restricted ? "r" : "t");
      const workspace = path.join(dir, "workspace");
      fs.cpSync(path.join(repoRoot, "tests", "fixtures"), workspace, { recursive: true });
      const userData = userDataFolder(dir, reverb, restricted);
      const env = { REVERB_TEST_GREP: grep, REVERB_TEST_CLI: reverb, REVERB_TEST_RESTRICTED: restricted ? "1" : "" };
      const launchArgs = [workspace, "--user-data-dir", userData, "--extensions-dir", path.join(cachePath, "extensions")];
      console.log(`\nRunning the ${restricted ? "Restricted Mode" : "trusted"} suites in ${workspace}`);
      if (restricted) await runRestricted(vscodeExecutablePath, launchArgs, env);
      else {
        await runTests({
          vscodeExecutablePath,
          extensionDevelopmentPath: extensionRoot,
          extensionTestsPath: path.join(__dirname, "index.js"),
          extensionTestsEnv: env,
          launchArgs,
        });
      }
    }
    fs.rmSync(temp, { recursive: true, force: true });
  } catch (e) {
    console.error(`The test workspaces were left in ${temp}.`);
    throw e;
  }
}

/** The CLI built from this checkout, behind a wrapper so it can be a single reverb.path. */
function reverbCommand(): string {
  const configuration = process.env.REVERB_CONFIGURATION || "Debug";
  const bin = path.join(repoRoot, "src", "Reverb.Cli", "bin", configuration, "net10.0");
  const dll = path.join(bin, "Reverb.Cli.dll");
  if (!fs.existsSync(dll)) {
    throw new Error(`The Reverb CLI isn't built (${dll} is missing). Run \`dotnet build Reverb.slnx -c ${configuration}\` in ${repoRoot} first.`);
  }
  // Node won't spawn a .cmd without a shell, so on Windows use the built apphost directly.
  if (process.platform === "win32") return path.join(bin, "Reverb.Cli.exe");
  const wrapper = path.join(extensionRoot, "test", "integration", "reverb");
  fs.chmodSync(wrapper, 0o755);
  return wrapper;
}

// reverb.path is machine scoped, so VS Code only reads it from user settings, not the workspace's.
function userDataFolder(dir: string, reverb: string, restricted: boolean): string {
  const userData = path.join(dir, "u");
  fs.mkdirSync(path.join(userData, "User"), { recursive: true });
  const settings: Record<string, unknown> = { "reverb.path": reverb, "workbench.startupEditor": "none", "git.enabled": false };
  // Open the untrusted folder in Restricted Mode without the trust dialog.
  if (restricted) settings["security.workspace.trust.startupPrompt"] = "never";
  fs.writeFileSync(path.join(userData, "User", "settings.json"), JSON.stringify(settings, null, 2));
  return userData;
}

// runTests always passes --disable-workspace-trust, which trusts everything, so Restricted Mode
// needs its own launch. These are the other flags runTests passes.
function runRestricted(exe: string, launchArgs: string[], env: Record<string, string>): Promise<void> {
  const args = [
    ...launchArgs,
    "--no-sandbox",
    "--disable-gpu-sandbox",
    "--disable-updates",
    "--skip-welcome",
    "--skip-release-notes",
    "--no-cached-data",
    `--extensionTestsPath=${path.join(__dirname, "index.js")}`,
    `--extensionDevelopmentPath=${extensionRoot}`,
  ];
  return new Promise((resolve, reject) => {
    const child = spawn(exe, args, { env: { ...process.env, ...env }, stdio: "inherit" });
    child.on("error", reject);
    child.on("exit", (code, signal) =>
      code === 0 ? resolve() : reject(new Error(`The Restricted Mode run failed (exit ${code ?? signal}).`)),
    );
  });
}

main().catch((e) => {
  console.error(e instanceof Error ? e.message : e);
  process.exit(1);
});
