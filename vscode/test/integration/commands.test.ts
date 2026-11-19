import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import { defaultUi } from "../../src/commands";
import type { ReverbApi } from "../../src/extension";
import { reverbExtension, closeAll, notebookText, openReverbNotebook, setAutoSave, waitFor, workspaceFile, outputText } from "./helpers";

const WAIT_FOREVER = "await Task.Delay(-1, Notebook.CancellationToken);";
const CHAIN = notebookText(["var a = 1;", "var b = a * 10;", "Console.WriteLine(b + 1);"]);

let fileCount = 0;

function api(): ReverbApi {
  const ext = reverbExtension<ReverbApi>();
  assert.ok(ext?.isActive, "the extension should be active once a notebook is open");
  assert.ok(ext.exports, "the extension only returns its test API in test mode");
  return ext.exports;
}

/** Writes a notebook to a new file, opens it and waits for its kernel. */
async function open(text: string): Promise<vscode.NotebookDocument> {
  const file = workspaceFile(`commands-${++fileCount}.cs`);
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
  const ranges = ids.map((id) => ({ start: cell(nb, id).index, end: cell(nb, id).index + 1 }));
  await vscode.commands.executeCommand("notebook.cell.execute", { ranges, document: nb.uri });
}

function text(c: vscode.NotebookCell): string {
  return outputText(c);
}

function ended(nb: vscode.NotebookDocument, id: string, success: boolean, timeoutMs = 30_000) {
  return waitFor(() => cell(nb, id).executionSummary?.success === success, `cell '${id}' to end with success ${success}`, timeoutMs);
}

function preambleText(nb: vscode.NotebookDocument): string {
  const lines = (nb.metadata.reverb?.preamble ?? []) as { text: string }[];
  return lines.map((l) => l.text).join("\n");
}

describe("commands", () => {
  // The tests check that a command makes the notebook dirty, then save it themselves.
  before(() => setAutoSave(false));
  after(() => setAutoSave(undefined));
  afterEach(async () => {
    Object.assign(api().ui, defaultUi);
    await closeAll();
  });

  it("add package writes the directive and rebuilds", async () => {
    const nb = await open(notebookText(['Console.WriteLine("hello_world".Humanize());']));
    assert.strictEqual(nb.isDirty, false, "opening should not change the notebook");
    api().ui.showInputBox = async (o) => (/name/i.test(o?.prompt ?? "") ? "Humanizer" : "2.14.1");
    await vscode.commands.executeCommand("reverb.addPackage");

    await waitFor(() => /#:package Humanizer@2\.14\.1/.test(preambleText(nb)), "the package in the notebook's metadata");
    assert.ok(nb.isDirty, "adding a package should make the notebook dirty");
    assert.ok(await nb.save());
    assert.match(fs.readFileSync(nb.uri.fsPath, "utf8"), /^#:package Humanizer@2\.14\.1$/m);

    // The kernel is rebuilt without running anything, so the cell is stale until it's run.
    await waitFor(() => api().session(nb.uri)?.kernelState === "ready" && api().session(nb.uri)?.packages?.length === 1, "the new kernel");
    assert.strictEqual(api().session(nb.uri)?.statusOf("c1"), "stale");
    assert.strictEqual(cell(nb, "c1").executionSummary?.success, undefined);
    // The code uses the package's extension, which needs `using Humanizer;`. Qualify it instead of editing setup.
    const edit = new vscode.WorkspaceEdit();
    const doc = cell(nb, "c1").document;
    edit.replace(doc.uri, new vscode.Range(0, 0, doc.lineCount, 0), 'Console.WriteLine(Humanizer.StringHumanizeExtensions.Humanize("hello_world"));');
    assert.ok(await vscode.workspace.applyEdit(edit));
    await run(nb, "c1");
    await ended(nb, "c1", true);
    assert.strictEqual(text(cell(nb, "c1")), "hello world\n");
  });

  it("undoing an added package puts it back, so the file and the kernel agree", async () => {
    const nb = await open(notebookText(["var a = 1;"]));
    api().ui.showInputBox = async (o) => (/name/i.test(o?.prompt ?? "") ? "Humanizer" : "2.14.1");
    await vscode.commands.executeCommand("reverb.addPackage");
    await waitFor(() => /Humanizer/.test(preambleText(nb)), "the package in the notebook's metadata");
    assert.ok(await nb.save());
    await waitFor(() => api().session(nb.uri)?.packages?.length === 1, "the server to list the package");

    let undone = false;
    const watch = vscode.workspace.onDidChangeNotebookDocument((e) => {
      if (e.notebook === nb && e.metadata && !/Humanizer/.test(preambleText(nb))) undone = true;
    });
    const errors = api().errors.length;
    try {
      await vscode.window.showNotebookDocument(nb);
      await vscode.commands.executeCommand("undo");
      await waitFor(() => undone, "undo to take the package out of the notebook's metadata", 5_000);
    } finally {
      watch.dispose();
    }

    // The session puts the server's preamble back and says how packages are changed.
    await waitFor(() => /Humanizer/.test(preambleText(nb)), "the package back in the notebook's metadata", 5_000);
    await waitFor(() => api().errors.length > errors, "a message about changing packages", 5_000);
    assert.match(api().errors[errors], /Reverb: Add Package, Remove Package or Toggle Lazy Mode/);
    if (nb.isDirty) assert.ok(await nb.save());
    assert.match(fs.readFileSync(nb.uri.fsPath, "utf8"), /^#:package Humanizer@2\.14\.1$/m);
    assert.deepStrictEqual(api().session(nb.uri)?.packages, [{ name: "Humanizer", version: "2.14.1" }]);
  });

  it("adding Reverb.Runtime says Reverb manages it", async () => {
    const nb = await open(CHAIN);
    api().ui.showInputBox = async () => "Reverb.Runtime";
    const errors = api().errors.length;
    await vscode.commands.executeCommand("reverb.addPackage");
    await waitFor(() => api().errors.length > errors, "an error message");
    assert.strictEqual(api().errors[errors], "Reverb.Runtime is managed by Reverb, so it can't be added or removed here.");
    assert.strictEqual(nb.isDirty, false);
  });

  for (const [name, text] of [
    ["only the header, no newline", "// reverb-notebook v1"],
    ["only the header and a newline", "// reverb-notebook v1\n"],
  ] as const) {
    it(`a file with ${name} isn't dirty after it opens`, async () => {
      const nb = await open(text);
      await new Promise((r) => setTimeout(r, 1000));
      assert.strictEqual(nb.isDirty, false);
    });
  }

  it("remove package", async () => {
    const withPackage = notebookText(["var a = 1;"]).replace("#:package Reverb.Runtime@0.1.0\n", "#:package Reverb.Runtime@0.1.0\n#:package Humanizer@2.14.1\n");
    const nb = await open(withPackage);
    assert.deepStrictEqual(api().session(nb.uri)?.packages, [{ name: "Humanizer", version: "2.14.1" }]);
    let offered: readonly string[] = [];
    api().ui.showQuickPick = async (items) => {
      offered = items;
      return "Humanizer";
    };
    await vscode.commands.executeCommand("reverb.removePackage");

    await waitFor(() => !/Humanizer/.test(preambleText(nb)), "the package to leave the notebook's metadata");
    assert.deepStrictEqual(offered, ["Humanizer"]);
    assert.ok(await nb.save());
    assert.doesNotMatch(fs.readFileSync(nb.uri.fsPath, "utf8"), /Humanizer/);
    await waitFor(() => api().session(nb.uri)?.kernelState === "ready" && api().session(nb.uri)?.packages?.length === 0, "the new kernel");

    // With nothing left to remove it says so.
    const errors = api().errors.length;
    await vscode.commands.executeCommand("reverb.removePackage");
    assert.match(api().errors[errors], /no packages to remove/);
  });

  it("toggle lazy writes the header flag", async () => {
    const nb = await open(CHAIN);
    assert.strictEqual(api().session(nb.uri)?.lazy, false);
    await vscode.commands.executeCommand("reverb.toggleLazy");
    await waitFor(() => api().session(nb.uri)?.lazy === true, "lazy mode to turn on");
    await waitFor(() => nb.isDirty, "the notebook to change");
    assert.ok(await nb.save());
    assert.match(fs.readFileSync(nb.uri.fsPath, "utf8"), /^\/\/ reverb-notebook v1 lazy$/m);

    await vscode.commands.executeCommand("reverb.toggleLazy");
    await waitFor(() => api().session(nb.uri)?.lazy === false, "lazy mode to turn off");
    await waitFor(() => nb.isDirty, "the notebook to change again");
    assert.ok(await nb.save());
    assert.match(fs.readFileSync(nb.uri.fsPath, "utf8"), /^\/\/ reverb-notebook v1$/m);
  });

  it("new notebook asks for a path and opens it", async () => {
    await open(CHAIN); // activates the extension
    const target = workspaceFile("brand-new.cs");
    fs.rmSync(target, { force: true });
    let asked = 0;
    api().ui.showSaveDialog = async (o) => {
      asked++;
      assert.ok(o?.filters && Object.values(o.filters).some((f) => f.includes("cs")), "the dialog should offer .cs files");
      return vscode.Uri.file(target);
    };
    await vscode.commands.executeCommand("reverb.newNotebook");
    assert.strictEqual(asked, 1);
    const nb = await waitFor(
      () => vscode.window.activeNotebookEditor?.notebook.uri.fsPath === target && vscode.window.activeNotebookEditor.notebook,
      "the new notebook to open",
    );
    assert.strictEqual(nb.notebookType, "reverb");
    const written = fs.readFileSync(target, "utf8");
    assert.match(written, /^#!\/usr\/bin\/env dotnet\n#:package Reverb\.Runtime@/);
    assert.match(written, /^\/\/ reverb-notebook v1$/m);
    assert.deepStrictEqual(nb.getCells().map((c) => c.metadata.reverb?.kind), ["setup", "markdown", "code", "code"]);
    assert.strictEqual(nb.isDirty, false);
    await waitFor(() => api().session(nb.uri)?.kernelState === "ready", "the new notebook's kernel");
  });

  it("new notebook asks before replacing a file the .cs was added to", async () => {
    await open(CHAIN); // activates the extension
    const target = workspaceFile("keep-me.cs");
    fs.writeFileSync(target, "Console.WriteLine(\"mine\");\n");
    api().ui.showSaveDialog = async () => vscode.Uri.file(target.slice(0, -".cs".length));
    const asked: string[] = [];
    let answer: string | undefined;
    api().ui.showWarningMessage = async (message, options, ...items) => {
      asked.push(message);
      assert.strictEqual(options.modal, true);
      assert.ok(answer === undefined || items.includes(answer));
      return answer;
    };

    // No: the file stays as it was and nothing opens.
    await vscode.commands.executeCommand("reverb.newNotebook");
    assert.deepStrictEqual(asked, ["Replace keep-me.cs?"]);
    assert.strictEqual(fs.readFileSync(target, "utf8"), "Console.WriteLine(\"mine\");\n");
    assert.notStrictEqual(vscode.window.activeNotebookEditor?.notebook.uri.fsPath, target);

    // Replace: the new notebook is written there.
    answer = "Replace";
    await vscode.commands.executeCommand("reverb.newNotebook");
    assert.strictEqual(asked.length, 2);
    assert.match(fs.readFileSync(target, "utf8"), /^\/\/ reverb-notebook v1$/m);
  });

  it("new notebook does nothing when the dialog is cancelled", async () => {
    await open(CHAIN);
    api().ui.showSaveDialog = async () => undefined;
    const before = vscode.window.activeNotebookEditor?.notebook.uri.toString();
    await vscode.commands.executeCommand("reverb.newNotebook");
    assert.strictEqual(vscode.window.activeNotebookEditor?.notebook.uri.toString(), before);
  });

  it("restart ends executions and the next run works", async () => {
    const nb = await open(notebookText(["var a = 1;", WAIT_FOREVER, "Console.WriteLine(a + 10);"]));
    await vscode.commands.executeCommand("notebook.execute", nb.uri);
    await waitFor(() => api().session(nb.uri)?.statusOf("c2") === "running", "c2 to be running");
    await ended(nb, "c1", true);
    assert.ok(api().session(nb.uri)?.executing.includes("c2"));

    await vscode.commands.executeCommand("reverb.restartKernel");
    // Ended by the command itself, before the new kernel has said anything.
    assert.deepStrictEqual(api().session(nb.uri)?.executing, []);
    await ended(nb, "c2", false, 5_000);
    await waitFor(() => api().session(nb.uri)?.kernelState === "ready", "the new kernel");
    for (const id of ["c1", "c2", "c3"]) {
      await waitFor(() => api().session(nb.uri)?.statusOf(id) === "stale", `cell '${id}' to be stale`);
      assert.strictEqual(api().cellStatus(cell(nb, id)), "Stale: run to update");
    }

    await run(nb, "c3");
    await waitFor(() => text(cell(nb, "c3")) === "11\n" && cell(nb, "c3").executionSummary?.success === true, "c3 to print 11 in the new kernel");
    assert.strictEqual(api().session(nb.uri)?.statusOf("c2"), "stale");
  });

  it("restart with nothing running makes every cell stale", async () => {
    const nb = await open(CHAIN);
    await run(nb, "c3");
    await ended(nb, "c3", true);
    assert.strictEqual(api().cellStatus(cell(nb, "c3")), undefined);
    await vscode.commands.executeCommand("reverb.restartKernel");
    await waitFor(() => api().cellStatus(cell(nb, "c3")) === "Stale: run to update", "c3 to be stale");
  });
});

describe("status bar", () => {
  afterEach(closeAll);

  it("shows stale on every stale code cell and the kernel state", async () => {
    const nb = await open(CHAIN);
    for (const id of ["c1", "c2", "c3"]) assert.strictEqual(api().cellStatus(cell(nb, id)), "Stale: run to update");
    assert.strictEqual(api().kernelStatus()?.text, "Reverb: ready");

    await run(nb, "c3");
    await ended(nb, "c3", true);
    for (const id of ["c1", "c2", "c3"]) assert.strictEqual(api().cellStatus(cell(nb, id)), undefined);
  });

  it("an upstream error shows the server's message naming the failing cell", async () => {
    const nb = await open(notebookText(["var a = 1;", "var b = a / (a - 1);", "Console.WriteLine(b);"], ["c1", "bad", "c3"]));
    await run(nb, "c3");
    await waitFor(() => api().session(nb.uri)?.statusOf("c3") === "upstream-error", "c3 to report an upstream error");
    const shown = await waitFor(() => api().cellStatus(cell(nb, "c3")), "the status text of c3");
    assert.match(shown, /'bad'/);
    assert.strictEqual(shown, api().session(nb.uri)?.messageOf("c3"));
    assert.strictEqual(api().cellStatus(cell(nb, "bad")), undefined, "the failing cell shows its own error, not a status");
  });

  it("disabled cells say so", async () => {
    const text = notebookText(["var a = 1;"]).replace("// %% id=c1", "// %% id=c1 disabled");
    const nb = await open(text);
    await run(nb, "c1");
    await waitFor(() => api().cellStatus(cell(nb, "c1")) === "Disabled", "c1 to say it's disabled");
  });
});

describe("open as notebook", () => {
  afterEach(closeAll);

  it("offers a CodeLens on a notebook text file, and opens it as a notebook", async () => {
    await open(CHAIN); // activates the extension
    const file = workspaceFile("as-text.cs");
    fs.writeFileSync(file, CHAIN);
    const doc = await vscode.workspace.openTextDocument(file);
    await vscode.window.showTextDocument(doc);
    const lenses = await waitFor(
      async () => {
        const l = await vscode.commands.executeCommand<vscode.CodeLens[]>("vscode.executeCodeLensProvider", doc.uri);
        return l.some((x) => x.command?.command === "reverb.openAsNotebook") && l;
      },
      "the CodeLens",
    );
    const lens = lenses.find((x) => x.command?.command === "reverb.openAsNotebook")!;
    assert.strictEqual(lens.range.start.line, 0);
    assert.strictEqual(lens.command?.title, "Open as Reverb notebook");

    await vscode.commands.executeCommand("reverb.openAsNotebook");
    await waitFor(() => vscode.window.activeNotebookEditor?.notebook.uri.fsPath === file, "the file to open as a notebook");
    assert.strictEqual(vscode.window.activeNotebookEditor?.notebook.notebookType, "reverb");
  });

  it("no CodeLens on a plain C# file", async () => {
    await open(CHAIN);
    const file = workspaceFile("plain.cs");
    fs.writeFileSync(file, "Console.WriteLine(1);\n");
    const doc = await vscode.workspace.openTextDocument(file);
    const lenses = await vscode.commands.executeCommand<vscode.CodeLens[]>("vscode.executeCodeLensProvider", doc.uri);
    assert.deepStrictEqual(lenses.filter((x) => x.command?.command === "reverb.openAsNotebook"), []);
  });
});
