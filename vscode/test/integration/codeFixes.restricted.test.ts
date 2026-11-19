import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import { closeAll, notebookText, openReverbNotebook, workspaceFile } from "./helpers";

describe("code fixes in Restricted Mode", () => {
  afterEach(closeAll);

  it("untrusted: no code actions", async () => {
    assert.strictEqual(vscode.workspace.isTrusted, false);
    const file = workspaceFile("fix-restricted.cs");
    fs.writeFileSync(file, notebookText(["var a = 2;", "using System.Text;\nvar sb = new StringBuilder().Append(a);"]));
    const nb = await openReverbNotebook(file);
    const c2 = nb.getCells().find((c) => c.metadata.reverb?.id === "c2");
    assert.ok(c2, "no cell 'c2'");
    await new Promise((r) => setTimeout(r, 1_000));
    const actions = await vscode.commands.executeCommand<vscode.CodeAction[]>(
      "vscode.executeCodeActionProvider",
      c2.document.uri,
      new vscode.Range(0, 0, 0, "using System.Text;".length),
    );
    assert.deepStrictEqual(actions.filter((a) => a.command?.command === "reverb.applyFix").map((a) => a.title), []);
  });
});
