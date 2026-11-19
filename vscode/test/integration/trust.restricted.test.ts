import * as assert from "node:assert";
import { execFileSync } from "node:child_process";
import * as fs from "node:fs";
import * as vscode from "vscode";
import type { ReverbApi } from "../../src/extension";
import { reverbExtension, closeAll, notebookText, openReverbNotebook, waitFor, workspaceFile, outputText } from "./helpers";

function api(): ReverbApi {
  const ext = reverbExtension<ReverbApi>();
  assert.ok(ext?.isActive && ext.exports, "the extension should be active in test mode");
  return ext.exports;
}

function text(c: vscode.NotebookCell): string {
  return outputText(c);
}

// VS Code itself asks for trust before it runs a cell in a restricted window, and that dialog
// can't be answered in a test. So these tests call the controller's execute handler directly,
// which is what VS Code calls once it lets a run through.
// A test instance can't be trusted from inside a Restricted Mode launch without UI, so the last
// test calls the function onDidGrantWorkspaceTrust invokes. It must stay the last one here.
describe("Restricted Mode", () => {
  afterEach(closeAll);

  let file: string;
  before(() => {
    file = workspaceFile("trust-chain.cs");
    fs.writeFileSync(file, notebookText(["var a = 1;", "var b = a * 10;", "Console.WriteLine(b + 1);"]));
  });

  it("untrusted: notebook opens, no kernel", async () => {
    assert.strictEqual(vscode.workspace.isTrusted, false);
    const nb = await openReverbNotebook(file);
    assert.ok(nb.getCells().some((c) => c.document.getText().includes("var a = 1;")), "the notebook should show its cells");
    await waitFor(() => api().kernelStatus()?.text === "Reverb: restricted", "the kernel item to say restricted", 5_000);
    // The notebook opened through the serializer, which needs reverb for format requests.
    await waitFor(() => api().sentTypes().length > 0, "the serializer to have talked to reverb", 10_000);
    await new Promise((r) => setTimeout(r, 1_000));
    assert.ok(!api().sentTypes().includes("notebook.open"), `sent: ${api().sentTypes().join(", ")}`);
    assert.strictEqual(api().session(nb.uri), undefined);
    const pid = api().processId();
    if (process.platform !== "win32" && pid !== undefined) {
      let children = "";
      try {
        children = execFileSync("pgrep", ["-P", String(pid)], { encoding: "utf8" }).trim();
      } catch {
        // pgrep exits 1 when there are no children
      }
      assert.strictEqual(children, "", "reverb should have no child process before the folder is trusted");
    }
  });

  it("untrusted: running says to trust the folder", async () => {
    const nb = await openReverbNotebook(file);
    const before = api().errors.length;
    const i = nb.getCells().find((c) => c.metadata.reverb?.id === "c3")!.index;
    await api().executeCells(nb, [nb.cellAt(i)]);
    await waitFor(() => api().errors.length > before, "the trust message", 5_000);
    assert.strictEqual(api().errors[before], "Trust this folder to run Reverb notebooks.");
    assert.ok(!api().sentTypes().includes("notebook.open"));
  });

  it("trusting starts the kernel", async () => {
    const nb = await openReverbNotebook(file);
    assert.strictEqual(api().session(nb.uri), undefined);
    // A cell added while nothing is open in reverb has no Reverb id yet.
    const edit = new vscode.WorkspaceEdit();
    const cell = new vscode.NotebookCellData(vscode.NotebookCellKind.Code, "Console.WriteLine(a + 100);", "reverb-csharp");
    edit.set(nb.uri, [vscode.NotebookEdit.insertCells(nb.cellCount, [cell])]);
    assert.ok(await vscode.workspace.applyEdit(edit));
    const added = nb.cellCount - 1;
    assert.strictEqual(nb.cellAt(added).metadata.reverb?.id, undefined);

    await api().simulateTrustGranted();
    await waitFor(() => api().session(nb.uri)?.kernelState === "ready", "the kernel to be ready");
    assert.ok(api().sentTypes().includes("notebook.open"));
    const i = nb.getCells().find((c) => c.metadata.reverb?.id === "c3")!.index;
    // Running through the handler doesn't select the controller, which an execution needs.
    await vscode.commands.executeCommand("notebook.selectKernel", { id: "reverb", extension: reverbExtension()!.id });
    await api().executeCells(nb, [nb.cellAt(i)]);
    const c3 = nb.cellAt(i);
    await waitFor(() => text(c3) === "11\n" && c3.executionSummary?.success === true, "c3 to print 11");

    // The added cell gets the id reverb gave it, without another edit, and it runs.
    const id = await waitFor(() => nb.cellAt(added).metadata.reverb?.id as string | undefined, "the added cell to get its id", 5_000);
    assert.notStrictEqual(id, "c3");
    await api().executeCells(nb, [nb.cellAt(added)]);
    await waitFor(() => text(nb.cellAt(added)) === "101\n" && nb.cellAt(added).executionSummary?.success === true, "the added cell to print 101");
  });
});
