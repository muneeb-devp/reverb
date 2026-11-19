import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import type { ReverbApi } from "../../src/extension";
import { MIME, type WidgetSpec } from "../../../frontend/src/protocol";
import { reverbExtension, closeAll, notebookText, openReverbNotebook, waitFor, workspaceFile, outputText } from "./helpers";

let fileCount = 0;

function api(): ReverbApi {
  const ext = reverbExtension<ReverbApi>();
  assert.ok(ext?.isActive, "the extension should be active once a notebook is open");
  assert.ok(ext.exports, "the extension only returns its test API in test mode");
  return ext.exports;
}

/** A notebook with a slider (c1) and a cell printing its value (c2), already run. */
async function open(): Promise<vscode.NotebookDocument> {
  const text = notebookText(['var s = Ui.Slider("n", min: 0, max: 100, value: 1);','Console.WriteLine($"value={s.Value}");']);
  const file = workspaceFile(`widgets-${++fileCount}.cs`);
  fs.writeFileSync(file, text);
  const nb = await openReverbNotebook(file);
  await waitFor(() => api().session(nb.uri)?.kernelState === "ready", "the kernel to be ready");
  await vscode.commands.executeCommand("notebook.execute", nb.uri);
  await waitFor(() => reader(nb) === "value=1", "the first run to print value=1");
  return nb;
}

function cell(nb: vscode.NotebookDocument, id: string): vscode.NotebookCell {
  const c = nb.getCells().find((x) => x.metadata.reverb?.id === id);
  assert.ok(c, `no cell '${id}'`);
  return c;
}

function reader(nb: vscode.NotebookDocument): string {
  return outputText(cell(nb, "c2")).trim();
}

function widgetId(nb: vscode.NotebookDocument): string {
  const item = cell(nb, "c1").outputs.flatMap((o) => o.items).find((i) => i.mime === MIME.widget);
  assert.ok(item, "the slider cell should have a widget output");
  const spec = JSON.parse(new TextDecoder().decode(item.data)) as WidgetSpec;
  assert.ok(spec.id, "the widget should be live");
  return spec.id;
}

const change = (widgetId: string, value: unknown) => ({ type: "widget.setValue", widgetId, value, kind: "slider" });

describe("widgets", () => {
  afterEach(closeAll);

  it("a changed value reaches the kernel and the widget cell's output is left alone", async () => {
    const nb = await open();
    const before = cell(nb, "c1").outputs[0];
    assert.strictEqual(api().handleWidgetMessage(nb.uri, change(widgetId(nb), 9)), true);
    await waitFor(() => reader(nb) === "value=9", "the reader to print value=9");
    assert.strictEqual(cell(nb, "c1").outputs[0], before, "the slider's output should not have been rewritten");
  });

  it("widget messages go to the notebook that sent them", async () => {
    const a = await open();
    const b = await open();
    assert.strictEqual(api().handleWidgetMessage(a.uri, change(widgetId(a), 9)), true);
    await waitFor(() => reader(a) === "value=9", "the first notebook to print value=9");
    await new Promise((r) => setTimeout(r, 500));
    assert.strictEqual(reader(b), "value=1");
  });

  it("a message that isn't a widget change sends nothing", async () => {
    const nb = await open();
    assert.strictEqual(api().handleWidgetMessage(nb.uri, { type: "nope" }), false);
  });
});
