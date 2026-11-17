// Loaded by VS Code as --extensionTestsPath. Runs the *.test.js files next to it: the
// *.restricted.test.js ones when run.ts started VS Code in Restricted Mode, the rest otherwise.
import * as fs from "node:fs";
import * as path from "node:path";
import Mocha from "mocha";
import * as vscode from "vscode";

export async function run(): Promise<void> {
  // Make sure the suites talk to the CLI built from this checkout, not an installed reverb.
  const configured = vscode.workspace.getConfiguration("reverb").get<string>("path");
  if (configured !== process.env.REVERB_TEST_CLI) {
    throw new Error(`reverb.path is '${configured}', expected '${process.env.REVERB_TEST_CLI}'. Check the user settings run.ts writes.`);
  }
  const restricted = process.env.REVERB_TEST_RESTRICTED === "1";
  if (vscode.workspace.isTrusted === restricted) {
    throw new Error(`The workspace should ${restricted ? "not " : ""}be trusted for these suites. Check how run.ts starts VS Code.`);
  }

  const mocha = new Mocha({ ui: "bdd", color: true, timeout: 60_000, grep: process.env.REVERB_TEST_GREP || undefined });
  for (const f of fs.readdirSync(__dirname).sort()) {
    if (f.endsWith(".test.js") && f.endsWith(".restricted.test.js") === restricted) mocha.addFile(path.join(__dirname, f));
  }
  return new Promise((resolve, reject) => {
    mocha.run((failures) => (failures ? reject(new Error(`${failures} integration test(s) failed.`)) : resolve()));
  });
}
