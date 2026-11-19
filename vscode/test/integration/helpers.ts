// Shared by the integration suites, which run inside VS Code.
import * as assert from "node:assert";
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";

/** Text with \n line endings: on Windows, what a cell prints and what's typed into a cell use \r\n. */
export function lf(text: string): string {
  return text.replace(/\r\n/g, "\n");
}

/** Everything a cell's outputs show, as text with \n line endings. */
export function outputText(c: vscode.NotebookCell): string {
  return lf(c.outputs.flatMap((o) => o.items.map((i) => new TextDecoder().decode(i.data))).join(""));
}

/** The test workspace, a fresh copy of tests/fixtures made by run.ts. */
export function workspaceFile(name: string): string {
  const folder = vscode.workspace.workspaceFolders?.[0];
  if (!folder) throw new Error("The integration tests need a workspace folder. Run them with `npm run test:integration`.");
  return path.join(folder.uri.fsPath, name);
}

/**
 * Opens a .cs file as a Reverb notebook in an editor. openNotebookDocument(uri) on its own also
 * picks `reverb` in the test instance, but only because nothing else claims *.cs; openWith names
 * the type and shows the editor, as a user opening it would see.
 */
export async function openReverbNotebook(file: string): Promise<vscode.NotebookDocument> {
  const uri = vscode.Uri.file(file);
  await vscode.commands.executeCommand("vscode.openWith", uri, "reverb");
  const nb = await vscode.workspace.openNotebookDocument(uri);
  if (nb.notebookType !== "reverb") throw new Error(`${file} opened as '${nb.notebookType}', not as a Reverb notebook.`);
  return nb;
}

/** Replaces the text of the cell with the given Reverb id. */
export async function editCell(nb: vscode.NotebookDocument, id: string, text: string): Promise<void> {
  const cell = nb.getCells().find((c) => c.metadata.reverb?.id === id);
  if (!cell) throw new Error(`No cell with id '${id}' in ${nb.uri.fsPath}.`);
  const doc = cell.document;
  const edit = new vscode.WorkspaceEdit();
  edit.replace(doc.uri, new vscode.Range(doc.positionAt(0), doc.positionAt(doc.getText().length)), text);
  if (!(await vscode.workspace.applyEdit(edit))) throw new Error(`Editing cell '${id}' failed.`);
}

/**
 * Opens a fixture, changes one cell's text from `before` to `after`, saves, and checks that the
 * file is the original with only that text changed.
 */
export async function editAndSave(name: string, cellId: string, before: string, after: string): Promise<void> {
  const file = workspaceFile(name);
  const original = fs.readFileSync(file);
  const nb = await openReverbNotebook(file);
  await editCell(nb, cellId, after);
  assert.ok(nb.isDirty, "the edit should make the notebook dirty");
  assert.ok(await nb.save(), "save should succeed");
  const expected = Buffer.from(original.toString("utf8").replace(before, after), "utf8");
  assert.notDeepStrictEqual(expected, original, `the fixture should contain '${before}'`);
  assert.deepStrictEqual(fs.readFileSync(file), expected);
}

/** Sets `reverb.autoSave` in the user settings. Undefined goes back to the default (on). */
export async function setAutoSave(on: boolean | undefined): Promise<void> {
  await vscode.workspace.getConfiguration("reverb").update("autoSave", on, vscode.ConfigurationTarget.Global);
}

/** Closes every editor without saving, so a failed test can't leave a save prompt open. */
export async function closeAll(): Promise<void> {
  for (let i = 0; i < 50 && vscode.window.tabGroups.all.some((g) => g.tabs.length > 0); i++) {
    await vscode.commands.executeCommand("workbench.action.revertAndCloseActiveEditor");
  }
}

/** Polls until `check` returns something truthy, or fails naming `what` after `timeoutMs`. */
export async function waitFor<T>(
  check: () => T | undefined | null | false | Promise<T | undefined | null | false>,
  what: string,
  timeoutMs = 30_000,
): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await check();
    if (value) return value;
    if (Date.now() > deadline) throw new Error(`Timed out after ${timeoutMs} ms waiting for ${what}.`);
    await new Promise((r) => setTimeout(r, 50));
  }
}

/** The same text as the C# test helper NotebookText.Build: the usual setup cell, then the cells. */
export function notebookText(cells: string[], ids?: string[]): string {
  let text = "#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1\n\n// %% setup\nusing Reverb;\nusing static Reverb.Notebook;\n\n";
  cells.forEach((source, i) => (text += `// %% id=${ids?.[i] ?? `c${i + 1}`}\n${source}\n\n`));
  return text;
}

/** This extension, found by package name so the tests do not depend on the publisher id. */
export function reverbExtension<T = unknown>(): vscode.Extension<T> | undefined {
  return vscode.extensions.all.find((e) => e.packageJSON.name === "reverb-notebooks") as vscode.Extension<T> | undefined;
}
