import * as assert from "node:assert";
import * as vscode from "vscode";
import type { ReverbApi } from "../../src/extension";
import { reverbExtension, closeAll, openReverbNotebook, waitFor, workspaceFile } from "./helpers";

function api(): ReverbApi {
  const ext = reverbExtension<ReverbApi>();
  assert.ok(ext?.isActive && ext.exports, "the extension should be active in test mode");
  return ext.exports;
}

describe("packages view in Restricted Mode", () => {
  afterEach(closeAll);

  it("is shown but empty, and changing packages asks for trust", async () => {
    await openReverbNotebook(workspaceFile("chain.cs"));
    await waitFor(() => api().packagesView.notebookActive(), "the notebook-active context key to be set", 5_000);
    assert.deepStrictEqual(api().packagesView.items(), []);
    const before = api().errors.length;
    await vscode.commands.executeCommand("reverb.addPackage");
    assert.strictEqual(api().errors[before], "Trust this folder to run Reverb notebooks.");
  });
});
