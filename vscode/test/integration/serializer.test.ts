import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import { closeAll, editAndSave, openReverbNotebook, setAutoSave, waitFor, workspaceFile } from "./helpers";

// Saving must not change bytes the user didn't change. These tests save when they choose to.
describe("serializer", () => {
  before(() => setAutoSave(false));
  after(() => setAutoSave(undefined));
  afterEach(closeAll);

  it("opens chain.cs as cells with Reverb metadata", async () => {
    const nb = await openReverbNotebook(workspaceFile("chain.cs"));
    assert.strictEqual(nb.notebookType, "reverb");
    assert.deepStrictEqual(
      nb.getCells().map((c) => [c.kind, c.document.languageId, c.metadata.reverb?.kind, c.metadata.reverb?.id, c.document.getText()]),
      [
        [vscode.NotebookCellKind.Code, "reverb-csharp", "setup", undefined, "using Reverb;"],
        [vscode.NotebookCellKind.Code, "reverb-csharp", "code", "a", "var a = 2;"],
        [vscode.NotebookCellKind.Code, "reverb-csharp", "code", "b", "var b = a * 10;"],
        [vscode.NotebookCellKind.Code, "reverb-csharp", "code", "c", "var c = b + 1;"],
        [vscode.NotebookCellKind.Code, "reverb-csharp", "code", "d", 'Console.WriteLine($"a={a} b={b} c={c}");'],
      ],
    );
    assert.deepStrictEqual(nb.metadata.reverb, {
      preamble: [
        { text: "#!/usr/bin/env dotnet", terminator: "\n" },
        { text: "#:package Reverb.Runtime@0.1.0", terminator: "\n" },
        { text: "// reverb-notebook v1", terminator: "\n" },
        { text: "", terminator: "\n" },
      ],
      newline: "\n",
      bom: false,
    });
  });

  it("opens markdown cells as markup cells", async () => {
    const nb = await openReverbNotebook(workspaceFile("markdown.cs"));
    const title = nb.getCells().find((c) => c.metadata.reverb?.id === "title")!;
    assert.strictEqual(title.kind, vscode.NotebookCellKind.Markup);
    assert.strictEqual(title.document.languageId, "markdown");
    assert.ok(title.document.getText().startsWith("# A notebook with prose"));
  });

  it("saving chain.cs changes only the edited line", () =>
    editAndSave("chain.cs", "b", "var b = a * 10;", "var b = a * 100;"));

  it("saving crlf.cs changes only the edited line and keeps CRLF", () =>
    editAndSave("crlf.cs", "b", "Console.WriteLine(a + 1);", "Console.WriteLine(a + 2);"));

  it("saving bom.cs keeps the BOM and the non-ASCII text", () =>
    editAndSave("bom.cs", "show", "Console.WriteLine(greeting.Length);", "Console.WriteLine(greeting);"));

  it("saving markdown.cs leaves the markdown cells alone", () =>
    editAndSave("markdown.cs", "value", "var value = 10;", "var value = 11;"));

  it("refuses a file that isn't UTF-8 rather than changing its bytes on save", async () => {
    const file = workspaceFile("latin1.cs");
    fs.writeFileSync(file, Buffer.from("// reverb-notebook v1\n\n// %% id=a\nvar s = \"caf\xe9\";\n", "latin1"));
    await assert.rejects(
      Promise.resolve(vscode.workspace.openNotebookDocument(vscode.Uri.file(file))),
      (e: Error) => e.message.includes("isn't valid UTF-8"),
    );
  });

  it("a cell added without metadata is saved as a new code cell with an id", async () => {
    const file = workspaceFile("independent.cs");
    const original = fs.readFileSync(file, "utf8");
    const nb = await openReverbNotebook(file);
    const edit = new vscode.WorkspaceEdit();
    edit.set(nb.uri, [
      vscode.NotebookEdit.insertCells(nb.cellCount, [
        new vscode.NotebookCellData(vscode.NotebookCellKind.Code, "var added = 1;", "reverb-csharp"),
      ]),
    ]);
    assert.ok(await vscode.workspace.applyEdit(edit));
    // Reverb gives the new cell an id by editing its metadata. Saving before that edit lands can
    // race it on a slow machine, so wait for it, as a user would see it.
    const added = nb.cellAt(nb.cellCount - 1);
    await waitFor(() => added.metadata.reverb?.id, "Reverb to give the new cell an id");
    if (nb.isDirty) assert.ok(await nb.save(), "save should succeed");
    await waitFor(() => !nb.isDirty, "the notebook to be saved", 5000);
    const saved = fs.readFileSync(file, "utf8");
    assert.ok(saved.startsWith(original.replace(/\n$/, "")), "the cells that were there keep their bytes");
    assert.match(saved.slice(original.length - 1), /^\n\n\/\/ %% id=[a-z0-9]+\nvar added = 1;\n$/);
  });
});
