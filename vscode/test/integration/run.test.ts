import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import type { ReverbApi } from "../../src/extension";
import { reverbExtension, closeAll, editCell, notebookText, openReverbNotebook, waitFor, workspaceFile, outputText, lf } from "./helpers";

// What the C# helper NotebookText.Build(["var a = 1;", "var b = a * 10;", "Console.WriteLine(b + 1);"]) writes.
const CHAIN =
  "#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1\n\n" +
  "// %% setup\nusing Reverb;\nusing static Reverb.Notebook;\n\n" +
  "// %% id=c1\nvar a = 1;\n\n" +
  "// %% id=c2\nvar b = a * 10;\n\n" +
  "// %% id=c3\nConsole.WriteLine(b + 1);\n\n";

const WAIT_FOREVER = "await Task.Delay(-1, Notebook.CancellationToken);";

let fileCount = 0;

function api(): ReverbApi {
  const ext = reverbExtension<ReverbApi>();
  assert.ok(ext?.isActive, "the extension should be active once a notebook is open");
  assert.ok(ext.exports, "the extension only returns its test API in test mode");
  return ext.exports;
}

/** Writes a notebook to a new file, opens it and waits for its kernel. */
async function open(text: string): Promise<vscode.NotebookDocument> {
  const file = workspaceFile(`run-${++fileCount}.cs`);
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

async function run(nb: vscode.NotebookDocument, ...ids: string[]): Promise<void> {
  const ranges = ids.map((id) => {
    const i = cell(nb, id).index;
    return { start: i, end: i + 1 };
  });
  await vscode.commands.executeCommand("notebook.cell.execute", { ranges, document: nb.uri });
}

function text(c: vscode.NotebookCell): string {
  return outputText(c);
}

/** Waits for the cell's execution to end with `success` after `afterOrder` (a newer run). */
function ended(nb: vscode.NotebookDocument, id: string, success: boolean, afterOrder = 0, timeoutMs = 30_000) {
  return waitFor(
    () => {
      const s = cell(nb, id).executionSummary;
      return s?.success === success && (s.executionOrder ?? 0) > afterOrder && s;
    },
    `cell '${id}' to end with success ${success}`,
    timeoutMs,
  );
}

describe("running", () => {
  afterEach(closeAll);

  it("opening does not run", async () => {
    const nb = await open(CHAIN);
    await new Promise((r) => setTimeout(r, 1000));
    for (const c of nb.getCells()) {
      assert.strictEqual(c.executionSummary?.success, undefined, `cell ${c.index} has a run result`);
      assert.strictEqual(c.executionSummary?.executionOrder, undefined, `cell ${c.index} has an execution order`);
      assert.strictEqual(c.outputs.length, 0);
    }
    assert.strictEqual(api().session(nb.uri)?.statusOf("c3"), "stale");
  });

  it("run c3 runs its upstream cells and prints 11", async () => {
    const nb = await open(CHAIN);
    const errors = api().errors.length;
    await run(nb, "c3");
    const c3 = await ended(nb, "c3", true);
    assert.strictEqual(text(cell(nb, "c3")), "11\n");
    assert.strictEqual(cell(nb, "c3").outputs[0].items[0].mime, "application/vnd.code.notebook.stdout");
    // VS Code only asked for c3; the engine ran c1 and c2 first.
    assert.strictEqual(cell(nb, "c1").executionSummary?.success, true);
    assert.strictEqual(cell(nb, "c2").executionSummary?.success, true);
    // A cell that's up to date runs again when asked.
    await run(nb, "c3");
    await ended(nb, "c3", true, c3.executionOrder);
    assert.strictEqual(text(cell(nb, "c3")), "11\n");
    assert.deepStrictEqual(api().errors.slice(errors), []);
  });

  it("editing c1 then running it reruns c3", async () => {
    const nb = await open(CHAIN);
    await run(nb, "c3");
    const before = await ended(nb, "c3", true);
    await editCell(nb, "c1", "var a = 3;");
    await run(nb, "c1");
    await ended(nb, "c3", true, before.executionOrder);
    assert.strictEqual(text(cell(nb, "c3")), "31\n");
  });

  it("a failing cell ends as failed and shows the error with its cell and line", async () => {
    const nb = await open(notebookText(["var a = 1;", 'throw new InvalidOperationException("boom");', "Console.WriteLine(a);"]));
    await run(nb, "c2");
    await ended(nb, "c2", false);
    const item = cell(nb, "c2").outputs[0].items[0];
    assert.strictEqual(item.mime, "application/vnd.code.notebook.error");
    const error = JSON.parse(new TextDecoder().decode(item.data));
    assert.strictEqual(error.name, "InvalidOperationException");
    assert.match(error.message, /boom\n {2}at cell 'c2', line 1/);
  });

  it("interrupt stops a Task.Delay cell", async () => {
    const nb = await open(notebookText([WAIT_FOREVER]));
    await run(nb, "c1");
    await waitFor(() => api().session(nb.uri)?.statusOf("c1") === "running", "c1 to be running");
    await vscode.commands.executeCommand("notebook.cancelExecution");
    await ended(nb, "c1", false, 0, 10_000);
    assert.strictEqual(api().session(nb.uri)?.statusOf("c1"), "stale");
  });

  it("process crash ends executions", async () => {
    // The kernel outlives a killed reverb until its cell finishes, so this cell does finish.
    const slow = "await Task.Delay(20_000, Notebook.CancellationToken);";
    const nb = await open(notebookText(["var a = 1;", slow, "var b = a * 10;", "Console.WriteLine(b + 1);"], ["c1", "slow", "c2", "c3"]));
    await vscode.commands.executeCommand("notebook.execute", nb.uri);
    await waitFor(() => api().session(nb.uri)?.statusOf("slow") === "running", "the slow cell to be running");
    const pid = api().processId();
    assert.ok(pid, "reverb should be running");
    const errorsBefore = api().errors.length;
    process.kill(pid, "SIGKILL");

    for (const id of ["slow", "c2", "c3"]) await ended(nb, id, false, -1, 10_000);
    // They end when the process exits, not later when the new kernel reports the cells as stale.
    assert.ok(["starting", "building", undefined].includes(api().session(nb.uri)?.kernelState), "ended before the new kernel was up");
    assert.strictEqual(cell(nb, "c1").executionSummary?.success, true, "c1 finished before the crash");
    await waitFor(() => api().errors.length > errorsBefore, "an error message");
    assert.match(api().errors[api().errors.length - 1], /Reverb stopped/);

    await waitFor(() => api().processId() !== undefined && api().processId() !== pid, "a new reverb process");
    await waitFor(() => api().session(nb.uri)?.kernelState === "ready", "the reopened notebook's kernel");
    await run(nb, "c3");
    await waitFor(() => cell(nb, "c3").executionSummary?.success === true && text(cell(nb, "c3")) === "11\n", "c3 to print 11 again");
  });

  it("deleting a queued cell ends its execution", async () => {
    const nb = await open(notebookText(["await Task.Delay(3000);", "var a = 1;", "Console.WriteLine(a + 1);"]));
    await vscode.commands.executeCommand("notebook.execute", nb.uri);
    await waitFor(() => api().session(nb.uri)?.statusOf("c1") === "running", "c1 to be running");
    assert.ok(api().session(nb.uri)?.executing.includes("c3"), "c3 should be queued");

    const index = cell(nb, "c3").index;
    const edit = new vscode.WorkspaceEdit();
    edit.set(nb.uri, [vscode.NotebookEdit.deleteCells(new vscode.NotebookRange(index, index + 1))]);
    assert.ok(await vscode.workspace.applyEdit(edit));
    await ended(nb, "c2", true, 0, 10_000);
    await waitFor(() => api().session(nb.uri)?.executing.length === 0, "no execution to be left open", 5_000);

    // Undo brings the cell back with its id. Running it uses a new execution, not the one that was ended.
    await vscode.commands.executeCommand("undo");
    await waitFor(() => nb.getCells().some((c) => c.metadata.reverb?.id === "c3"), "undo to bring c3 back", 5_000);
    await run(nb, "c3");
    await waitFor(() => cell(nb, "c3").executionSummary?.success === true && text(cell(nb, "c3")) === "2\n", "the restored c3 to print 2");
  });

  it("an untitled notebook asks to be saved before it runs", async () => {
    await open(CHAIN); // activates the extension
    const data = new vscode.NotebookData([new vscode.NotebookCellData(vscode.NotebookCellKind.Code, "Console.WriteLine(1);", "reverb-csharp")]);
    const nb = await vscode.workspace.openNotebookDocument("reverb", data);
    await vscode.window.showNotebookDocument(nb);
    const before = api().errors.length;
    await vscode.commands.executeCommand("notebook.cell.execute", { ranges: [{ start: 0, end: 1 }], document: nb.uri });
    const shown = await waitFor(() => api().errors.length > before && api().errors[api().errors.length - 1], "an error message", 5_000);
    assert.match(shown, /^Save Untitled-\d+(\.cs)? to a file to run its cells\.$/);
    assert.strictEqual(nb.cellAt(0).executionSummary?.success, undefined);
  });

  it("large stream output", async () => {
    const nb = await open(notebookText(["for (var i = 0; i < 20000; i++) Console.WriteLine(i);"]));
    const started = Date.now();
    await run(nb, "c1");
    await ended(nb, "c1", true, 0, 30_000);
    assert.ok(Date.now() - started < 30_000);
    const outputs = cell(nb, "c1").outputs;
    const last = outputs[outputs.length - 1];
    const lastText = lf(new TextDecoder().decode(last.items[last.items.length - 1].data));
    assert.ok(lastText.endsWith("19999\n"), `the output ends with ${JSON.stringify(lastText.slice(-20))}`);
    assert.ok(text(cell(nb, "c1")).startsWith("0\n1\n2\n"));
  });
});
