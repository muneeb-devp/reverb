import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import { closeAll, editAndSave, editCell, openReverbNotebook, setAutoSave, workspaceFile } from "./helpers";

// Reading and saving run no notebook code, so they work before the folder is trusted.
describe("serializer in Restricted Mode", () => {
  before(() => setAutoSave(false));
  after(() => setAutoSave(undefined));
  afterEach(closeAll);

  it("opens and saves a notebook in an untrusted folder", async () => {
    assert.strictEqual(vscode.workspace.isTrusted, false);
    await editAndSave("chain.cs", "b", "var b = a * 10;", "var b = a * 100;");
  });

  it("saving twice gives a pasted cell the same id both times", async () => {
    // Without a kernel nothing writes the new id back into the cell, so every save sends it without one.
    const file = workspaceFile("independent.cs");
    const nb = await openReverbNotebook(file);
    const edit = new vscode.WorkspaceEdit();
    edit.set(nb.uri, [
      vscode.NotebookEdit.insertCells(1, [new vscode.NotebookCellData(vscode.NotebookCellKind.Code, "var pasted = 1;", "reverb-csharp")]),
    ]);
    assert.ok(await vscode.workspace.applyEdit(edit));
    assert.ok(await nb.save());
    const first = fs.readFileSync(file);
    assert.strictEqual(nb.cellAt(1).metadata.reverb, undefined, "the pasted cell still has no Reverb metadata");

    // A change and its reverse, so there is something to save but the cells end up the same.
    const id = nb.cellAt(2).metadata.reverb.id;
    const text = nb.cellAt(2).document.getText();
    await editCell(nb, id, text + " ");
    await editCell(nb, id, text);
    assert.ok(nb.isDirty, "the notebook should be dirty so the second save writes");
    assert.ok(await nb.save());
    assert.deepStrictEqual(fs.readFileSync(file), first);
  });
});
