import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import type { ReverbApi } from "../../src/extension";
import { closeAll, notebookText, openReverbNotebook, reverbExtension, setAutoSave, workspaceFile } from "./helpers";

describe("cell attributes in Restricted Mode", () => {
  before(() => setAutoSave(false));
  after(() => setAutoSave(undefined));
  afterEach(closeAll);

  it("untrusted: attributes change and save, and no kernel is started", async () => {
    assert.strictEqual(vscode.workspace.isTrusted, false);
    const file = workspaceFile("attrs-restricted.cs");
    const original = notebookText(["var a = 1;", "var b = a * 10;"]);
    fs.writeFileSync(file, original);
    const nb = await openReverbNotebook(file);
    const c2 = nb.getCells().find((c) => c.metadata.reverb?.id === "c2");
    assert.ok(c2, "no cell 'c2'");

    await vscode.commands.executeCommand("reverb.toggleDisabled", c2);
    assert.ok(nb.isDirty);
    assert.ok(await nb.save());
    assert.strictEqual(fs.readFileSync(file, "utf8"), original.replace("// %% id=c2\n", "// %% id=c2 disabled\n"));

    const api = reverbExtension<ReverbApi>()?.exports;
    assert.ok(api, "the extension should return its test API");
    assert.strictEqual(api.session(nb.uri), undefined, "no session should have been made");
    assert.deepStrictEqual(api.sentTypes().filter((t) => t.startsWith("notebook.") || t.startsWith("kernel.")), []);
  });
});
