import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import { defaultUi } from "../../src/commands";
import type { ReverbApi } from "../../src/extension";
import { reverbExtension, closeAll, notebookText, openReverbNotebook, setAutoSave, waitFor, workspaceFile } from "./helpers";

const CHAIN = notebookText(["var a = 1;", "var b = a * 10;", "Console.WriteLine(b + 1);"]);

let fileCount = 0;

function api(): ReverbApi {
  const ext = reverbExtension<ReverbApi>();
  assert.ok(ext?.isActive, "the extension should be active once a notebook is open");
  assert.ok(ext.exports, "the extension only returns its test API in test mode");
  return ext.exports;
}

async function open(text: string): Promise<vscode.NotebookDocument> {
  const file = workspaceFile(`deps-${++fileCount}.cs`);
  fs.writeFileSync(file, text);
  const nb = await openReverbNotebook(file);
  await waitFor(() => api().session(nb.uri)?.kernelState === "ready", "the kernel to be ready");
  return nb;
}

function cell(nb: vscode.NotebookDocument, id: string): vscode.NotebookCell {
  const c = nb.getCells().find((x) => x.metadata.reverb?.id === id);
  assert.ok(c, `no cell '${id}'`);
  return c;
}

const texts = (c: vscode.NotebookCell) => api().cellDependencies(c).map((i) => i.text);

describe("dependencies", () => {
  before(() => setAutoSave(false));
  after(() => setAutoSave(undefined));
  afterEach(async () => {
    Object.assign(api().ui, defaultUi);
    await closeAll();
  });

  async function runAll(nb: vscode.NotebookDocument) {
    await vscode.commands.executeCommand("notebook.execute", nb.uri);
    await waitFor(() => cell(nb, "c3").executionSummary?.success === true, "c3 to finish");
    await waitFor(() => texts(cell(nb, "c2")).length === 2, "c2 to have both items");
  }

  it("shows what each cell of a chain uses and what uses it", async () => {
    const nb = await open(CHAIN);
    await runAll(nb);
    assert.deepStrictEqual(texts(cell(nb, "c2")), ["uses 1 cell", "used by 1 cell"]);
    assert.deepStrictEqual(texts(cell(nb, "c1")), ["used by 1 cell"]);
    assert.deepStrictEqual(texts(cell(nb, "c3")), ["uses 1 cell"]);
    assert.strictEqual(api().cellDependencies(cell(nb, "c2"))[0].tooltip, "Uses a from cell 2");
    assert.strictEqual(api().cellDependencies(cell(nb, "c2"))[1].tooltip, "Used by: cell 4");
  });

  it("labels a cell by its name", async () => {
    const nb = await open(CHAIN.replace("// %% id=c1\n", "// %% id=c1 name=load\n"));
    await runAll(nb);
    assert.strictEqual(api().cellDependencies(cell(nb, "c2"))[0].tooltip, "Uses a from load");
  });

  it("drops the item when a cell stops reading", async () => {
    const nb = await open(CHAIN);
    await runAll(nb);
    const edit = new vscode.WorkspaceEdit();
    edit.replace(cell(nb, "c3").document.uri, new vscode.Range(0, 0, 0, 100), "Console.WriteLine(1);");
    await vscode.workspace.applyEdit(edit);
    await waitFor(() => texts(cell(nb, "c3")).length === 0, "c3's item to go");
    assert.deepStrictEqual(texts(cell(nb, "c2")), ["uses 1 cell"]);
  });

  it("offers the related cells and selects the one picked", async () => {
    const nb = await open(CHAIN);
    await runAll(nb);
    await vscode.window.showNotebookDocument(nb);
    let offered: string[] = [];
    api().ui.showQuickPick = async (items) => ((offered = items), items.find((i) => i.includes(String(cell(nb, "c3").index + 1))));
    await vscode.commands.executeCommand("reverb.showDependencies", cell(nb, "c2"));
    assert.strictEqual(offered.length, 2);
    const editor = vscode.window.activeNotebookEditor;
    assert.strictEqual(editor?.selection.start, cell(nb, "c3").index);
  });

  it("says so when a cell has no dependencies", async () => {
    const nb = await open(notebookText(["var a = 1;", "var b = 2;"]));
    await vscode.window.showNotebookDocument(nb);
    const shown: string[] = [];
    api().ui.showInformationMessage = async (m) => void shown.push(m);
    await vscode.commands.executeCommand("reverb.showDependencies", cell(nb, "c1"));
    assert.deepStrictEqual(shown, ["cell 2 doesn't use or feed any other cell."]);
  });
});
