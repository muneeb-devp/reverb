import * as assert from "node:assert";
import * as vscode from "vscode";
import type { ReverbApi } from "../../src/extension";
import { reverbExtension, closeAll, openReverbNotebook, workspaceFile } from "./helpers";

function api(): ReverbApi {
  const ext = reverbExtension<ReverbApi>();
  assert.ok(ext?.isActive && ext.exports, "the extension should be active in test mode");
  return ext.exports;
}

// Runs before trust.restricted.test.ts, which grants trust to the rest of the run.
describe("widget messages in Restricted Mode", () => {
  afterEach(closeAll);

  it("a widget change isn't sent", async () => {
    assert.strictEqual(vscode.workspace.isTrusted, false);
    const nb = await openReverbNotebook(workspaceFile("chain.cs"));
    const sent = api().sentTypes().length;
    assert.strictEqual(api().handleWidgetMessage(nb.uri, { type: "widget.setValue", widgetId: "w1", value: 3, kind: "slider" }), false);
    assert.deepStrictEqual(api().sentTypes().slice(sent), []);
  });
});
