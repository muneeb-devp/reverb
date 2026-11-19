import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import type { ReverbApi } from "../../src/extension";
import { reverbExtension, closeAll, notebookText, openReverbNotebook, waitFor, workspaceFile, outputText, lf } from "./helpers";

// The notebook ClientOwnedHostTests uses for RVB004: a using in a code cell.
const MISPLACED_USING = notebookText(["var a = 2;", "using System.Text;\nvar sb = new StringBuilder().Append(a);\nConsole.WriteLine(sb);"]);

const ANONYMOUS = "var q = new[] { 1, 2 }.Select(x => new { X = x, Name = x.ToString() }).ToList();";
const RECORD_FIX = "Use a record instead of the anonymous type";

type Fix = vscode.CodeAction & { command: vscode.Command };

function api(): ReverbApi {
  const ext = reverbExtension<ReverbApi>();
  assert.ok(ext?.isActive && ext.exports, "the extension should be active and return its test API");
  return ext.exports;
}

function cell(nb: vscode.NotebookDocument, id: string): vscode.NotebookCell {
  const c = nb.getCells().find((x) => (x.metadata.reverb?.kind === "setup" ? "setup" : x.metadata.reverb?.id) === id);
  assert.ok(c, `no cell '${id}'`);
  return c;
}

function output(c: vscode.NotebookCell): string {
  return outputText(c);
}

/** The file split at its cell markers: the lines above the cells, then one piece per cell. */
function pieces(text: string): string[] {
  return text.split(/^(?=\/\/ %%)/m);
}

/** Writes a notebook, opens it, waits for its kernel and selects Reverb as its kernel, so runs show. */
async function openReady(name: string, text: string): Promise<{ file: string; nb: vscode.NotebookDocument }> {
  const file = workspaceFile(name);
  fs.writeFileSync(file, text);
  const nb = await openReverbNotebook(file);
  await waitFor(() => api().session(nb.uri)?.kernelState === "ready", "the kernel to be ready");
  await vscode.commands.executeCommand("notebook.selectKernel", { id: "reverb", extension: reverbExtension()!.id });
  return { file, nb };
}

/** Waits for the diagnostic `code` on the cell and returns its quick fix titled `title`. */
async function quickFix(nb: vscode.NotebookDocument, cellId: string, code: string, title: string): Promise<Fix> {
  const uri = cell(nb, cellId).document.uri;
  const diagnostic = await waitFor(() => vscode.languages.getDiagnostics(uri).find((d) => d.code === code), `${code} on ${cellId}`);
  const actions = await vscode.commands.executeCommand<vscode.CodeAction[]>("vscode.executeCodeActionProvider", uri, diagnostic.range);
  const fix = actions.find((a) => a.title === title);
  assert.ok(fix?.command, `no fix '${title}' among ${actions.map((a) => a.title).join(", ") || "no actions"}`);
  return fix as Fix;
}

function apply(fix: Fix): Thenable<unknown> {
  return vscode.commands.executeCommand(fix.command.command, ...(fix.command.arguments ?? []));
}

/** Waits for the cell to run and print `text`, and says what it did instead when it doesn't. */
async function printed(nb: vscode.NotebookDocument, cellId: string, text: string): Promise<void> {
  await waitFor(() => output(cell(nb, cellId)) === text && cell(nb, cellId).executionSummary?.success, `${cellId} to run and print ${JSON.stringify(text)}`).catch(
    (e: Error) => {
      const c = cell(nb, cellId);
      throw new Error(
        `${e.message} It printed '${output(c)}', summary ${JSON.stringify(c.executionSummary)}, status ${api().session(nb.uri)?.statusOf(cellId)}, errors: ${api().errors.join(" | ")}`,
      );
    },
  );
}

describe("code fixes", () => {
  afterEach(closeAll);

  it("moves a using to the setup cell, runs the cell and saves only the cells it changed", async () => {
    const { file, nb } = await openReady("fix-using.cs", MISPLACED_USING);
    const fix = await quickFix(nb, "c2", "RVB004", "Move to the setup cell");
    assert.strictEqual(fix.kind?.value, vscode.CodeActionKind.QuickFix.value);
    assert.strictEqual(fix.isPreferred, true);

    await apply(fix);
    assert.match(cell(nb, "setup").document.getText(), /using System\.Text;/);
    assert.doesNotMatch(cell(nb, "c2").document.getText(), /using System\.Text;/);
    const c2 = cell(nb, "c2").document.uri;
    await waitFor(() => !vscode.languages.getDiagnostics(c2).some((d) => d.code === "RVB004"), "RVB004 to go away");
    await printed(nb, "c2", "2\n");

    assert.ok(await nb.save(), "save should succeed");
    const before = pieces(MISPLACED_USING);
    const after = pieces(fs.readFileSync(file, "utf8"));
    assert.strictEqual(after.length, before.length, after.join("|"));
    assert.strictEqual(after[0], before[0], "the lines above the cells");
    assert.strictEqual(after[2], before[2], "c1");
    assert.match(after[1], /using System\.Text;/);
    assert.notStrictEqual(after[3], before[3]);
    assert.doesNotMatch(after[3], /using System\.Text;/);
  });

  it("shows Reverb's reason and changes nothing when there's no setup cell for the using", async () => {
    // Opening a file without a setup cell gives it one, so it's deleted here, as a user could.
    const { nb } = await openReady("fix-no-setup.cs", MISPLACED_USING);
    const edit = new vscode.WorkspaceEdit();
    edit.set(nb.uri, [vscode.NotebookEdit.deleteCells(new vscode.NotebookRange(0, 1))]);
    assert.ok(await vscode.workspace.applyEdit(edit));
    assert.ok(nb.getCells().every((c) => c.metadata.reverb?.kind !== "setup"), "the setup cell should be gone");
    const count = nb.cellCount;
    const errors = api().errors.length;

    await apply(await quickFix(nb, "c2", "RVB004", "Move to the setup cell"));
    assert.ok(api().errors.length > 0, "no error was shown");
    assert.strictEqual(
      api().errors.at(-1),
      `The fix for cell ${cell(nb, "c2").index + 1} in fix-no-setup.cs wasn't applied. This notebook has no setup cell to move the using into. Save the notebook and open it again, which adds a setup cell, then try the fix again.`,
    );
    assert.strictEqual(nb.cellCount, count);
    assert.match(cell(nb, "c2").document.getText(), /using System\.Text;/);
  });

  it("moves a statement out of the setup cell into a new code cell after it", async () => {
    // notebookText always writes the usual setup cell, so this notebook is written out here.
    const text =
      "#:package Reverb.Runtime@0.1.0\n// reverb-notebook v1\n\n" +
      "// %% setup\nusing Reverb;\nConsole.WriteLine(0);\n\n" +
      "// %% id=c1\nvar a = 1;\n\n";
    const { file, nb } = await openReady("fix-new-cell.cs", text);
    const count = nb.cellCount;

    await apply(await quickFix(nb, "setup", "RVB004", "Move to a new code cell"));
    assert.strictEqual(nb.cellCount, count + 1, api().errors.join(" | "));
    assert.strictEqual(cell(nb, "setup").document.getText(), "using Reverb;");
    const added = nb.cellAt(1);
    assert.strictEqual(added.metadata.reverb?.kind, "code");
    assert.strictEqual(added.document.getText(), "Console.WriteLine(0);");
    const id = added.metadata.reverb?.id as string | undefined;
    assert.ok(id, "the new cell should have the id Reverb gave it");
    await printed(nb, id, "0\n");

    assert.ok(await nb.save(), "save should succeed");
    const before = pieces(text);
    const after = pieces(fs.readFileSync(file, "utf8"));
    assert.strictEqual(after.length, before.length + 1, after.join("|"));
    assert.strictEqual(after[0], before[0], "the lines above the cells");
    assert.doesNotMatch(after[1], /Console/);
    assert.strictEqual(after[2], `// %% id=${id}\nConsole.WriteLine(0);\n\n`);
    assert.strictEqual(after[3], before[2], "c1");
  });

  it("adds a types cell for a record when the notebook has none", async () => {
    const text = notebookText([ANONYMOUS, "Console.WriteLine(q.Count);"]);
    const { file, nb } = await openReady("fix-record.cs", text);
    const count = nb.cellCount;

    await apply(await quickFix(nb, "c2", "RVB002", RECORD_FIX));
    assert.strictEqual(nb.cellCount, count + 1);
    const types = nb.cellAt(nb.cellCount - 1);
    assert.strictEqual(types.metadata.reverb?.kind, "types");
    assert.strictEqual(types.document.getText(), "record QRow(int X, string Name);");
    assert.strictEqual(cell(nb, "c1").document.getText(), "var q = new[] { 1, 2 }.Select(x => new QRow(x, x.ToString())).ToList();");
    const c2 = cell(nb, "c2").document.uri;
    await waitFor(() => !vscode.languages.getDiagnostics(c2).some((d) => d.code === "RVB002"), "RVB002 to go away");
    await printed(nb, "c2", "2\n");

    assert.ok(await nb.save(), "save should succeed");
    const before = pieces(text);
    const after = pieces(fs.readFileSync(file, "utf8"));
    assert.strictEqual(after.length, before.length + 1, after.join("|"));
    assert.strictEqual(after[0], before[0], "the lines above the cells");
    assert.strictEqual(after[1], before[1], "setup");
    assert.notStrictEqual(after[2], before[2], "c1");
    assert.strictEqual(after[3], before[3], "c2");
    assert.match(after[4], /^\/\/ %% types\nrecord QRow\(int X, string Name\);\n/);
  });

  it("one undo takes the whole fix back", async () => {
    const { nb } = await openReady("fix-undo.cs", notebookText([ANONYMOUS, "Console.WriteLine(q.Count);"]));
    const count = nb.cellCount;
    await apply(await quickFix(nb, "c2", "RVB002", RECORD_FIX));
    assert.strictEqual(nb.cellCount, count + 1);
    assert.notStrictEqual(cell(nb, "c1").document.getText(), ANONYMOUS);

    await vscode.commands.executeCommand("undo");
    await waitFor(() => nb.cellCount === count && cell(nb, "c1").document.getText() === ANONYMOUS, "the types cell to go and c1 to get its text back", 5_000).catch(
      (e: Error) => {
        throw new Error(`${e.message} There are ${nb.cellCount} cells and c1 is '${cell(nb, "c1").document.getText()}'.`);
      },
    );
  });

  /**
   * Asks for the RVB004 fix and types into c1 while it's worked out. `synced` says whether the
   * typing reaches the server before it answers (it refuses the fix) or not (the extension does).
   */
  async function typeDuringFix(name: string, synced: boolean): Promise<void> {
    const { nb } = await openReady(name, MISPLACED_USING);
    const fix = await quickFix(nb, "c2", "RVB004", "Move to the setup cell");
    const session = api().session(nb.uri)!;
    const setup = cell(nb, "setup").document.getText();
    const errors = api().errors.length;

    // The command sends its request before it returns to the caller, so this edit comes after it.
    let done = false;
    const applying = Promise.resolve(apply(fix)).finally(() => (done = true));
    const c1 = cell(nb, "c1").document;
    const edit = new vscode.WorkspaceEdit();
    edit.insert(c1.uri, c1.positionAt(c1.getText().length), "\nvar typed = 1;");
    assert.ok(await vscode.workspace.applyEdit(edit));
    if (synced) session.flush();
    // Each keystroke puts the sync off again, so someone still typing hasn't synced.
    else while (!done) {
      session.sync();
      await new Promise((r) => setTimeout(r, 20));
    }
    await applying;

    assert.ok(api().errors.length > 0, "no error was shown");
    assert.strictEqual(
      api().errors.at(-1),
      `The fix for cell ${cell(nb, "c2").index + 1} in ${name} wasn't applied. The notebook changed while Reverb was working out the fix. Try the fix again.`,
    );
    assert.strictEqual(cell(nb, "setup").document.getText(), setup);
    assert.match(cell(nb, "c2").document.getText(), /using System\.Text;/);
    assert.strictEqual(lf(cell(nb, "c1").document.getText()), "var a = 2;\nvar typed = 1;");
  }

  it("applies nothing when Reverb refuses a fix because the cells changed", () => typeDuringFix("fix-changed-synced.cs", true));

  it("applies nothing when the cells changed but hadn't synced when the fix came back", () => typeDuringFix("fix-changed-typing.cs", false));

  it("offers nothing for a diagnostic without a fix", async () => {
    const file = workspaceFile("fix-none.cs");
    fs.writeFileSync(file, notebookText(["var a = 1;", "var b = a * missing;"]));
    const nb = await openReverbNotebook(file);
    await waitFor(() => api().session(nb.uri)?.kernelState === "ready", "the kernel to be ready");
    const c2 = cell(nb, "c2");
    const diagnostic = await waitFor(() => vscode.languages.getDiagnostics(c2.document.uri).find((d) => d.code === "CS0103"), "CS0103 on c2");
    const actions = await vscode.commands.executeCommand<vscode.CodeAction[]>("vscode.executeCodeActionProvider", c2.document.uri, diagnostic.range);
    assert.deepStrictEqual(actions.filter((a) => a.command?.command === "reverb.applyFix").map((a) => a.title), []);
  });
});
