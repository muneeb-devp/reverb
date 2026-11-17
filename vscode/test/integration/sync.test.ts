import * as assert from "node:assert";
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import type { ReverbApi } from "../../src/extension";
import { reverbExtension, closeAll, editCell, notebookText, openReverbNotebook, setAutoSave, waitFor, workspaceFile } from "./helpers";

const CHAIN = notebookText(["var a = 1;", "var b = a * 10;", "Console.WriteLine(b + 1);"]);
const SETUP = "// %% setup\nusing Reverb;\nusing static Reverb.Notebook;\n\n";

let fileCount = 0;

function api(): ReverbApi {
  const ext = reverbExtension<ReverbApi>();
  assert.ok(ext?.isActive, "the extension should be active once a notebook is open");
  assert.ok(ext.exports, "the extension only returns its test API in test mode");
  return ext.exports;
}

/** Writes a notebook to a new file, opens it and waits for its kernel. */
async function open(text: string): Promise<vscode.NotebookDocument> {
  const file = workspaceFile(`sync-${++fileCount}.cs`);
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

async function run(nb: vscode.NotebookDocument, id: string): Promise<void> {
  const i = cell(nb, id).index;
  await vscode.commands.executeCommand("notebook.cell.execute", { ranges: [{ start: i, end: i + 1 }], document: nb.uri });
}

function text(c: vscode.NotebookCell): string {
  return c.outputs.flatMap((o) => o.items.map((i) => new TextDecoder().decode(i.data))).join("");
}

/** Runs a cell and returns what it printed once it has ended successfully. */
async function output(nb: vscode.NotebookDocument, id: string): Promise<string> {
  const before = cell(nb, id).executionSummary?.executionOrder ?? 0;
  await run(nb, id);
  await waitFor(() => {
    const s = cell(nb, id).executionSummary;
    return s?.success === true && (s.executionOrder ?? 0) > before;
  }, `cell '${id}' to run`);
  return text(cell(nb, id));
}

function file(nb: vscode.NotebookDocument): string {
  return fs.readFileSync(nb.uri.fsPath, "utf8");
}

/** The ids on the file's cell markers, in order. */
function markerIds(text: string): string[] {
  return [...text.matchAll(/^\/\/ %% id=(\S+)/gm)].map((m) => m[1]);
}

function cellIds(nb: vscode.NotebookDocument): (string | undefined)[] {
  return nb.getCells().map((c) => c.metadata.reverb?.id);
}

function kinds(nb: vscode.NotebookDocument): (string | undefined)[] {
  return nb.getCells().map((c) => c.metadata.reverb?.kind);
}

async function apply(nb: vscode.NotebookDocument, ...edits: vscode.NotebookEdit[]): Promise<void> {
  const edit = new vscode.WorkspaceEdit();
  edit.set(nb.uri, edits);
  assert.ok(await vscode.workspace.applyEdit(edit), "the edit should apply");
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

describe("keeping VS Code and the file in step", () => {
  afterEach(async () => {
    await setAutoSave(undefined);
    await closeAll();
  });

  it("typing autosaves within two seconds", async () => {
    const nb = await open(CHAIN);
    await editCell(nb, "c1", "var a = 2;");
    await waitFor(() => file(nb) === CHAIN.replace("var a = 1;", "var a = 2;"), "the file to have the new text", 2000);
    assert.strictEqual(nb.isDirty, false);
  });

  it("an untouched notebook isn't saved", async () => {
    // Written before notebooks listed Reverb.Runtime, so the notebook VS Code shows has a line the file doesn't.
    const old = CHAIN.replace("#:package Reverb.Runtime@0.1.0\n", "");
    const nb = await open(old);
    const written = fs.statSync(nb.uri.fsPath).mtimeMs;
    await sleep(1500);
    assert.strictEqual(nb.isDirty, false);
    assert.strictEqual(fs.statSync(nb.uri.fsPath).mtimeMs, written);
    assert.strictEqual(file(nb), old);
  });

  it("a lazy mode change is saved at once", async () => {
    const nb = await open(CHAIN);
    await vscode.commands.executeCommand("reverb.toggleLazy");
    await waitFor(() => /^\/\/ reverb-notebook v1 lazy$/m.test(file(nb)), "the header flag in the file", 2000);
    await waitFor(() => !nb.isDirty, "the notebook to be saved", 2000);
  });

  it("a file changed on disk shows up", async () => {
    const nb = await open(CHAIN);
    const changed = CHAIN.replace("var a = 1;", "var a = 2;");
    fs.writeFileSync(nb.uri.fsPath, changed);
    await waitFor(() => cell(nb, "c1").document.getText() === "var a = 2;", "c1 to show the text from disk", 5000);
    assert.strictEqual(await output(nb, "c3"), "21\n");

    // Nothing was saved in reply: the notebook is clean and the file is as the test wrote it.
    const written = fs.statSync(nb.uri.fsPath).mtimeMs;
    await sleep(1500);
    assert.strictEqual(nb.isDirty, false);
    assert.strictEqual(fs.statSync(nb.uri.fsPath).mtimeMs, written);
    assert.strictEqual(file(nb), changed);
  });

  it("a header changed on disk reaches the kernel", async () => {
    // Only a reload carries the lines above the cells; a cell sync doesn't.
    const nb = await open(CHAIN);
    assert.strictEqual(api().session(nb.uri)?.lazy, false);
    fs.writeFileSync(nb.uri.fsPath, CHAIN.replace("// reverb-notebook v1\n", "// reverb-notebook v1 lazy\n"));
    await waitFor(() => api().session(nb.uri)?.lazy === true, "lazy mode from the file", 5000);
    await sleep(1500);
    assert.strictEqual(nb.isDirty, false);
  });

  it("pasting a cell gives it a new id", async () => {
    const nb = await open(CHAIN);
    const c1 = cell(nb, "c1");
    // A duplicate made through the API keeps every metadata key, the Reverb id too.
    const copy = new vscode.NotebookCellData(c1.kind, c1.document.getText(), c1.document.languageId);
    copy.metadata = c1.metadata;
    await apply(nb, vscode.NotebookEdit.insertCells(c1.index + 1, [copy]));

    const ids = await waitFor(() => {
      const ids = cellIds(nb).slice(1);
      return ids[0] === "c1" && ids[1] && ids[1] !== "c1" && ids;
    }, "the copy to get its own id", 5000);
    assert.deepStrictEqual(ids.slice(2), ["c2", "c3"]);
    await waitFor(() => JSON.stringify(markerIds(file(nb))) === JSON.stringify(ids), "the file to have both ids", 5000);
    assert.strictEqual(new Set(markerIds(file(nb))).size, 4);
    await waitFor(() => !nb.isDirty, "the notebook to be saved", 5000);
    // The server has the cell under the same id. (Running it fails: it declares `a` again.)
    await waitFor(() => api().session(nb.uri)?.statusOf(ids[1]!), "the server to report on the copy", 5000);

    // Every cell has its id now, so saving again keeps the bytes.
    const saved = fs.readFileSync(nb.uri.fsPath);
    const c2 = cell(nb, "c2").document.getText();
    await editCell(nb, "c2", c2 + " ");
    await editCell(nb, "c2", c2);
    assert.ok(nb.isDirty, "the notebook should be dirty so the save writes");
    assert.ok(await nb.save());
    assert.deepStrictEqual(fs.readFileSync(nb.uri.fsPath), saved);
  });

  it("applying a correction doesn't sync it back", async () => {
    const nb = await open(CHAIN);
    const session = api().session(nb.uri)!;
    const before = session.syncsSent;
    await apply(nb, vscode.NotebookEdit.insertCells(2, [new vscode.NotebookCellData(vscode.NotebookCellKind.Code, "var pasted = 1;", "reverb-csharp")]));
    await waitFor(() => nb.cellAt(2).metadata.reverb?.id, "the pasted cell to get its id", 5000);
    await sleep(500);
    assert.strictEqual(session.syncsSent, before + 1, "only the paste should be synced");
  });

  it("File: Revert after the file changed on disk gives the server what the file has", async () => {
    // VS Code 1.146 only rereads the file on Revert when it changed on disk; otherwise Revert keeps
    // what the notebook has and just marks it clean. When it does reread, the notebook was dirty
    // before the change, and a sync would carry only the cells: the kernel would keep lazy mode.
    await setAutoSave(false);
    const nb = await open(CHAIN);
    await vscode.commands.executeCommand("reverb.toggleLazy");
    await waitFor(() => api().session(nb.uri)?.lazy === true && nb.isDirty, "lazy mode on and unsaved");
    const changed = CHAIN.replace("var a = 1;", "var a = 2;");
    fs.writeFileSync(nb.uri.fsPath, changed);

    await vscode.commands.executeCommand("workbench.action.files.revert");
    await waitFor(() => cell(nb, "c1").document.getText() === "var a = 2;" && !nb.isDirty, "the notebook to have the file's text", 5000);
    await waitFor(() => api().session(nb.uri)?.lazy === false, "the server to have the file's header", 5000);
    assert.strictEqual(await output(nb, "c3"), "21\n");
    assert.strictEqual(nb.isDirty, false, "the old header shouldn't be written back");
    assert.strictEqual(file(nb), changed);
  });

  it("deleting the setup cell keeps a valid file", async () => {
    const nb = await open(CHAIN);
    await apply(nb, vscode.NotebookEdit.deleteCells(new vscode.NotebookRange(0, 1)));
    await waitFor(() => file(nb) === CHAIN.replace(SETUP, ""), "the file without the setup cell", 5000);
    assert.strictEqual(await output(nb, "c3"), "11\n");
    assert.deepStrictEqual(kinds(nb), ["code", "code", "code"]);
  });

  it("a cell added after the types cell moves above it", async () => {
    const nb = await open(notebookText(["var p = new P(2);"]) + "// %% types\nrecord P(int X);\n");
    await apply(
      nb,
      vscode.NotebookEdit.insertCells(nb.cellCount, [new vscode.NotebookCellData(vscode.NotebookCellKind.Code, "Console.WriteLine(p.X + 1);", "reverb-csharp")]),
    );
    await waitFor(() => kinds(nb).join() === "setup,code,code,types" && cellIds(nb)[2], "the types cell to be last again", 5000);
    const added = cellIds(nb)[2]!;
    assert.strictEqual(nb.cellAt(2).document.getText(), "Console.WriteLine(p.X + 1);");
    await waitFor(() => /\/\/ %% types\nrecord P\(int X\);\n$/.test(file(nb)) && markerIds(file(nb)).includes(added), "the file to end with the types cell", 5000);
    assert.strictEqual(await output(nb, added), "3\n");
  });

  it("moving the setup cell down puts it back first", async () => {
    const nb = await open(CHAIN);
    const editor = await vscode.window.showNotebookDocument(nb);
    editor.selections = [new vscode.NotebookRange(0, 1)];
    await vscode.commands.executeCommand("notebook.cell.moveDown");
    await waitFor(() => kinds(nb).join() === "setup,code,code,code" && !nb.isDirty, "setup to be first again", 5000);
    assert.deepStrictEqual(cellIds(nb).slice(1), ["c1", "c2", "c3"]);
    assert.strictEqual(file(nb), CHAIN);
  });

  it("rename follows the file", async () => {
    const nb = await open(notebookText(["Console.WriteLine(Notebook.Folder);"]));
    const folder = workspaceFile(`moved-${++fileCount}`);
    fs.mkdirSync(folder);
    const target = vscode.Uri.file(path.join(folder, "moved.cs"));
    const edit = new vscode.WorkspaceEdit();
    edit.renameFile(nb.uri, target);
    assert.ok(await vscode.workspace.applyEdit(edit));

    // VS Code closes the notebook at the old path and opens one at the new path. In the test
    // instance no editor shows it, and cells only run in an editor, so the test opens one.
    const moved = await waitFor(
      () => vscode.workspace.notebookDocuments.find((d) => d.uri.fsPath === target.fsPath && !d.isClosed),
      "the notebook at its new path",
      5000,
    );
    assert.ok(nb.isClosed, "the notebook at the old path should be closed");
    await vscode.window.showNotebookDocument(moved);
    await waitFor(() => api().session(moved.uri)?.kernelState === "ready", "the kernel for the new path");
    const printed = (await output(moved, "c1")).trimEnd();
    assert.ok([folder, fs.realpathSync(folder)].includes(printed), `Notebook.Folder was '${printed}', expected '${folder}'`);
    assert.strictEqual(api().session(nb.uri), undefined);
  });

  it("renaming while a cell runs ends its execution", async () => {
    const nb = await open(notebookText(["await Task.Delay(-1, Notebook.CancellationToken);"]));
    const session = api().session(nb.uri)!;
    await run(nb, "c1");
    await waitFor(() => session.statusOf("c1") === "running", "c1 to be running");
    assert.deepStrictEqual(session.executing, ["c1"]);

    const folder = workspaceFile(`moved-${++fileCount}`);
    fs.mkdirSync(folder);
    const edit = new vscode.WorkspaceEdit();
    edit.renameFile(nb.uri, vscode.Uri.file(path.join(folder, "moved.cs")));
    assert.ok(await vscode.workspace.applyEdit(edit));
    await waitFor(() => session.executing.length === 0, "the execution to end", 5000);
    assert.strictEqual(api().session(nb.uri), undefined);
  });

  it("a notebook reverb lost says so once and opens again on the next run", async () => {
    const nb = await open(CHAIN);
    const session = api().session(nb.uri)!;
    // Close it in reverb behind the session's back, as if its host had gone.
    (session as unknown as { proc: { send(m: object): void } }).proc.send({ type: "notebook.close", notebook: session.path });
    const errors = api().errors.length;
    await editCell(nb, "c1", "var a = 2;");
    await waitFor(() => api().session(nb.uri) === undefined, "the session to be dropped", 5000);
    // More edits don't reach reverb, so they don't bring more errors.
    await editCell(nb, "c1", "var a = 3;");
    await sleep(500);
    await editCell(nb, "c1", "var a = 4;");
    await sleep(500);
    const shown = api().errors.slice(errors);
    assert.strictEqual(shown.length, 1, `errors: ${JSON.stringify(shown)}`);
    assert.match(shown[0], /isn't open in Reverb/);
    assert.match(shown[0], /sync-\d+\.cs/);
    assert.match(shown[0], /Run a cell/);

    assert.strictEqual(await output(nb, "c3"), "41\n");
    assert.ok(api().session(nb.uri), "running should open the notebook again");
  });

  it("autosave off leaves the file alone", async () => {
    await setAutoSave(false);
    const nb = await open(CHAIN);
    await editCell(nb, "c1", "var a = 2;");
    await sleep(3000);
    assert.strictEqual(file(nb), CHAIN);
    assert.strictEqual(nb.isDirty, true);
  });
});
