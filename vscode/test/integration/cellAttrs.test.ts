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

/** Writes a notebook to a new file, opens it and waits for its kernel. */
async function open(text: string): Promise<vscode.NotebookDocument> {
  const file = workspaceFile(`cellattrs-${++fileCount}.cs`);
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

function setupCell(nb: vscode.NotebookDocument): vscode.NotebookCell {
  const c = nb.getCells().find((x) => x.metadata.reverb?.kind === "setup");
  assert.ok(c, "no setup cell");
  return c;
}

function attrs(nb: vscode.NotebookDocument, id: string): { key: string; value?: string | null }[] {
  return cell(nb, id).metadata.reverb?.attrs ?? [];
}

describe("cell attributes", () => {
  before(() => setAutoSave(false));
  after(() => setAutoSave(undefined));
  afterEach(async () => {
    Object.assign(api().ui, defaultUi);
    await closeAll();
  });

  it("toggling disabled changes the status, the next cell, and only that cell's marker line on save", async () => {
    const nb = await open(CHAIN);
    const original = fs.readFileSync(nb.uri.fsPath, "utf8");
    assert.strictEqual(nb.isDirty, false);

    await vscode.commands.executeCommand("reverb.toggleDisabled", cell(nb, "c2"));
    assert.deepStrictEqual(attrs(nb, "c2"), [{ key: "disabled" }]);
    assert.ok(nb.isDirty, "toggling should make the notebook dirty");
    await waitFor(() => api().session(nb.uri)?.statusOf("c2") === "disabled", "c2 to be disabled");
    assert.strictEqual(api().cellStatus(cell(nb, "c2")), "Disabled");
    // c3 reads b, which is declared by the disabled cell, so it can't run either.
    await waitFor(() => api().session(nb.uri)?.statusOf("c3") === "upstream-error", "c3 to report it can't run");
    assert.ok(api().cellStatus(cell(nb, "c3")), "c3 should say why it can't run");

    assert.ok(await nb.save());
    assert.strictEqual(fs.readFileSync(nb.uri.fsPath, "utf8"), original.replace("// %% id=c2\n", "// %% id=c2 disabled\n"));

    await vscode.commands.executeCommand("reverb.toggleDisabled", cell(nb, "c2"));
    assert.deepStrictEqual(attrs(nb, "c2"), []);
    assert.ok(await nb.save());
    assert.strictEqual(fs.readFileSync(nb.uri.fsPath, "utf8"), original);
  });

  it("naming a cell shows the name under it and writes it to the marker line", async () => {
    const nb = await open(CHAIN);
    const original = fs.readFileSync(nb.uri.fsPath, "utf8");
    api().ui.showInputBox = async () => "load";
    await vscode.commands.executeCommand("reverb.nameCell", cell(nb, "c1"));

    assert.deepStrictEqual(attrs(nb, "c1"), [{ key: "name", value: "load" }]);
    assert.strictEqual(api().cellName(cell(nb, "c1")), "load");
    assert.strictEqual(api().cellName(cell(nb, "c2")), undefined);
    assert.ok(await nb.save());
    assert.strictEqual(fs.readFileSync(nb.uri.fsPath, "utf8"), original.replace("// %% id=c1\n", "// %% id=c1 name=load\n"));

    // An empty name removes it.
    api().ui.showInputBox = async () => "";
    await vscode.commands.executeCommand("reverb.nameCell", cell(nb, "c1"));
    assert.deepStrictEqual(attrs(nb, "c1"), []);
    assert.strictEqual(api().cellName(cell(nb, "c1")), undefined);
  });

  it("naming offers the current name, keeps it when cancelled, and quotes one with spaces", async () => {
    const nb = await open(CHAIN);
    let offered: string | undefined = "unset";
    api().ui.showInputBox = async (o) => ((offered = o?.value), "load data");
    await vscode.commands.executeCommand("reverb.nameCell", cell(nb, "c1"));
    assert.strictEqual(offered, undefined);
    api().ui.showInputBox = async (o) => ((offered = o?.value), undefined);
    await vscode.commands.executeCommand("reverb.nameCell", cell(nb, "c1"));
    assert.strictEqual(offered, "load data");
    assert.deepStrictEqual(attrs(nb, "c1"), [{ key: "name", value: "load data" }]);
    assert.ok(await nb.save());
    assert.match(fs.readFileSync(nb.uri.fsPath, "utf8"), /^\/\/ %% id=c1 name="load data"$/m);
  });

  it("toggling hide code saves hide_code and collapses or expands the input", async () => {
    const nb = await open(CHAIN);
    const original = fs.readFileSync(nb.uri.fsPath, "utf8");
    await vscode.commands.executeCommand("reverb.toggleHideCode", cell(nb, "c1"));
    assert.deepStrictEqual(attrs(nb, "c1"), [{ key: "hide_code" }]);
    assert.strictEqual(api().inputCollapseRequested(cell(nb, "c1")), true);
    assert.ok(await nb.save());
    assert.strictEqual(fs.readFileSync(nb.uri.fsPath, "utf8"), original.replace("// %% id=c1\n", "// %% id=c1 hide_code\n"));
    await vscode.commands.executeCommand("reverb.toggleHideCode", cell(nb, "c1"));
    assert.deepStrictEqual(attrs(nb, "c1"), []);
    assert.strictEqual(api().inputCollapseRequested(cell(nb, "c1")), false);
  });

  it("collapses and expands the input again when an undo or redo flips hide_code", async () => {
    const nb = await open(CHAIN);
    await vscode.commands.executeCommand("reverb.toggleHideCode", cell(nb, "c1"));
    assert.strictEqual(api().inputCollapseRequested(cell(nb, "c1")), true);
    await vscode.commands.executeCommand("undo");
    await waitFor(() => api().inputCollapseRequested(cell(nb, "c1")) === false, "the undo to expand c1's input");
    assert.deepStrictEqual(attrs(nb, "c1"), []);
    await vscode.commands.executeCommand("redo");
    await waitFor(() => api().inputCollapseRequested(cell(nb, "c1")) === true, "the redo to collapse c1's input");
  });

  it("collapses hidden cells when the notebook opens, and no others", async () => {
    const text = notebookText(["var a = 1;", "var b = 2;"]).replace("// %% id=c1\n", "// %% id=c1 hide_code\n");
    const nb = await open(text);
    await waitFor(() => api().inputCollapseRequested(cell(nb, "c1")) === true, "c1's input to be collapsed");
    assert.strictEqual(api().inputCollapseRequested(cell(nb, "c2")), undefined);
    assert.strictEqual(nb.isDirty, false, "collapsing is a view setting and shouldn't change the notebook");
  });

  it("uses the selected cells when run from the palette", async () => {
    const nb = await open(CHAIN);
    const editor = vscode.window.activeNotebookEditor;
    assert.ok(editor);
    editor.selection = new vscode.NotebookRange(cell(nb, "c2").index, cell(nb, "c2").index + 1);
    await vscode.commands.executeCommand("reverb.toggleDisabled");
    assert.deepStrictEqual(attrs(nb, "c2"), [{ key: "disabled" }]);
    assert.deepStrictEqual(attrs(nb, "c1"), []);
  });

  it("refuses setup cells with a message, and leaves them alone", async () => {
    const nb = await open(CHAIN);
    const before = api().errors.length;
    for (const command of ["reverb.toggleDisabled", "reverb.toggleHideCode", "reverb.nameCell"]) {
      api().ui.showInputBox = async () => "x";
      await vscode.commands.executeCommand(command, setupCell(nb));
    }
    const shown = api().errors.slice(before);
    assert.strictEqual(shown.length, 3);
    for (const m of shown) assert.match(m, /setup/i);
    assert.deepStrictEqual(setupCell(nb).metadata.reverb?.attrs, []);
    assert.strictEqual(nb.isDirty, false);
    // The error log is capped at 100 and other suites count on room in it, so give back what was added.
    (api().errors as string[]).splice(before);
  });
});
