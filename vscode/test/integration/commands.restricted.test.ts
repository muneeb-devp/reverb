import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import type { ReverbApi } from "../../src/extension";
import { reverbExtension, closeAll, openReverbNotebook, waitFor, workspaceFile } from "./helpers";

function api(): ReverbApi {
  const ext = reverbExtension<ReverbApi>();
  assert.ok(ext?.isActive && ext.exports, "the extension should be active in test mode");
  return ext.exports;
}

// Commands that need a kernel say so in an untrusted folder. Making and opening notebooks doesn't need one.
describe("commands in Restricted Mode", () => {
  afterEach(closeAll);

  for (const command of ["reverb.restartKernel", "reverb.toggleLazy", "reverb.addPackage", "reverb.removePackage"]) {
    it(`${command} asks for trust`, async () => {
      assert.strictEqual(vscode.workspace.isTrusted, false);
      await openReverbNotebook(workspaceFile("chain.cs"));
      const before = api().errors.length;
      await vscode.commands.executeCommand(command);
      assert.strictEqual(api().errors[before], "Trust this folder to run Reverb notebooks.");
      assert.strictEqual(api().session(vscode.Uri.file(workspaceFile("chain.cs"))), undefined);
    });
  }

  it("the kernel item says the folder is restricted", async () => {
    await openReverbNotebook(workspaceFile("chain.cs"));
    await waitFor(() => api().kernelStatus()?.text === "Reverb: restricted", "the kernel item to say restricted", 5_000);
  });

  it("new notebook and open as notebook work", async () => {
    await openReverbNotebook(workspaceFile("chain.cs"));
    const target = workspaceFile("restricted-new.cs");
    fs.rmSync(target, { force: true });
    api().ui.showSaveDialog = async () => vscode.Uri.file(target);
    await vscode.commands.executeCommand("reverb.newNotebook");
    await waitFor(() => vscode.window.activeNotebookEditor?.notebook.uri.fsPath === target, "the new notebook to open");
    assert.match(fs.readFileSync(target, "utf8"), /^\/\/ reverb-notebook v1$/m);

    const doc = await vscode.workspace.openTextDocument(workspaceFile("chain.cs"));
    await vscode.window.showTextDocument(doc);
    await vscode.commands.executeCommand("reverb.openAsNotebook");
    await waitFor(() => vscode.window.activeNotebookEditor?.notebook.uri.fsPath === workspaceFile("chain.cs"), "chain.cs to open as a notebook");
  });
});
