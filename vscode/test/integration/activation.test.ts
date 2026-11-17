import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import { reverbExtension, closeAll, notebookText, waitFor, workspaceFile } from "./helpers";

// This file sorts before the others, so nothing has activated the extension yet.
describe("activation", () => {
  afterEach(closeAll);

  it("a notebook file opened as text in a fresh window gets the CodeLens", async () => {
    const ext = reverbExtension();
    assert.ok(ext, "the extension should be installed in the test instance");
    assert.strictEqual(ext.isActive, false, "no other suite should have activated the extension before this one");

    const file = workspaceFile("fresh-text.cs");
    fs.writeFileSync(file, notebookText(["var a = 1;"]));
    const doc = await vscode.workspace.openTextDocument(file);
    await vscode.window.showTextDocument(doc);

    await waitFor(() => ext.isActive, "opening a C# file to activate the extension");
    const lens = await waitFor(
      async () => {
        const lenses = await vscode.commands.executeCommand<vscode.CodeLens[]>("vscode.executeCodeLensProvider", doc.uri);
        return lenses.find((l) => l.command?.command === "reverb.openAsNotebook");
      },
      "the Open as Reverb notebook CodeLens",
    );
    assert.strictEqual(lens.range.start.line, 0);
  });
});
