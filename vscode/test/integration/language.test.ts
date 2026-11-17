import * as assert from "node:assert";
import * as fs from "node:fs";
import * as vscode from "vscode";
import type { ReverbApi } from "../../src/extension";
import { reverbExtension, closeAll, notebookText, openReverbNotebook, waitFor, workspaceFile } from "./helpers";

let fileCount = 0;

function api(): ReverbApi {
  const ext = reverbExtension<ReverbApi>();
  assert.ok(ext?.isActive && ext.exports, "the extension should be active and return its test API");
  return ext.exports;
}

async function open(text: string, name = `lang-${++fileCount}.cs`): Promise<vscode.NotebookDocument> {
  const file = workspaceFile(name);
  fs.writeFileSync(file, text);
  return ready(await openReverbNotebook(file));
}

async function ready(nb: vscode.NotebookDocument): Promise<vscode.NotebookDocument> {
  await waitFor(() => api().session(nb.uri)?.kernelState === "ready", "the kernel to be ready");
  return nb;
}

function cell(nb: vscode.NotebookDocument, id: string): vscode.NotebookCell {
  const c = nb.getCells().find((x) => x.metadata.reverb?.id === id);
  assert.ok(c, `no cell '${id}'`);
  return c;
}

function atEnd(c: vscode.NotebookCell): vscode.Position {
  return c.document.positionAt(c.document.getText().length);
}

function diagnosticsOn(c: vscode.NotebookCell): vscode.Diagnostic[] {
  return vscode.languages.getDiagnostics(c.document.uri);
}

describe("language services", () => {
  afterEach(closeAll);

  it("completions list a variable from an earlier cell", async () => {
    const nb = await open(notebookText(["var a = 1;", "var b = a * 10;", "var z = a"]));
    const c3 = cell(nb, "c3");
    const list = await vscode.commands.executeCommand<vscode.CompletionList>("vscode.executeCompletionItemProvider", c3.document.uri, atEnd(c3));
    const a = list.items.find((i) => (typeof i.label === "string" ? i.label : i.label.label) === "a");
    assert.ok(a, `'a' is missing from ${list.items.length} completions`);
    assert.strictEqual(a.kind, vscode.CompletionItemKind.Variable);
  });

  it("hover shows the type", async () => {
    const nb = await open(notebookText(["var a = 1;", "var b = a * 10;"]));
    const c2 = cell(nb, "c2");
    const hovers = await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", c2.document.uri, new vscode.Position(0, 8));
    const text = hovers.flatMap((h) => h.contents.map((c) => (typeof c === "string" ? c : c.value))).join("\n");
    assert.match(text, /int/);
  });

  it("signature help for Math.Max", async () => {
    const nb = await open(notebookText(["Math.Max("]));
    const c1 = cell(nb, "c1");
    const help = await vscode.commands.executeCommand<vscode.SignatureHelp>("vscode.executeSignatureHelpProvider", c1.document.uri, atEnd(c1), "(");
    assert.ok(help && help.signatures.length > 0, "no signatures came back");
    assert.ok(help.signatures.every((s) => s.label.includes("Max")), help.signatures.map((s) => s.label).join(" | "));
    assert.ok(help.signatures.some((s) => s.parameters.length === 2));
  });

  it("a compile error appears on the cell", async () => {
    const nb = await open(notebookText(["var a = 1;", "var b = a * missing;"]));
    const c2 = cell(nb, "c2");
    const found = await waitFor(() => diagnosticsOn(c2).find((d) => d.code === "CS0103"), "CS0103 on c2");
    assert.strictEqual(diagnosticsOn(c2).length, 1);
    assert.strictEqual(found.severity, vscode.DiagnosticSeverity.Error);
    assert.strictEqual(found.source, "reverb");
    assert.strictEqual(found.range.start.line, 0);
    assert.strictEqual(found.range.start.character, 12);
    assert.deepStrictEqual(diagnosticsOn(cell(nb, "c1")), []);
  });

  it("RVB005 appears as a warning", async () => {
    const file = workspaceFile("diag-mutation.cs");
    const nb = await ready(await openReverbNotebook(file));
    const change = cell(nb, "change");
    const found = await waitFor(() => diagnosticsOn(change).find((d) => d.code === "RVB005"), "RVB005 on the change cell");
    assert.strictEqual(found.severity, vscode.DiagnosticSeverity.Warning);
    assert.strictEqual(found.source, "reverb");
  });

  it("fixing the error clears the diagnostic, and closing the notebook clears the rest", async () => {
    const nb = await open(notebookText(["var a = 1;", "var b = a * missing;"]));
    const c2 = cell(nb, "c2");
    await waitFor(() => diagnosticsOn(c2).length > 0, "a diagnostic on c2");
    const uri = c2.document.uri;
    const edit = new vscode.WorkspaceEdit();
    edit.replace(uri, new vscode.Range(0, 0, 0, c2.document.getText().length), "var b = a * 2;");
    assert.ok(await vscode.workspace.applyEdit(edit));
    await waitFor(() => diagnosticsOn(c2).length === 0, "the diagnostic to clear");

    const edit2 = new vscode.WorkspaceEdit();
    edit2.replace(uri, new vscode.Range(0, 0, 0, c2.document.getText().length), "var b = a * missing;");
    assert.ok(await vscode.workspace.applyEdit(edit2));
    await waitFor(() => diagnosticsOn(c2).length > 0, "the diagnostic to come back");
    await closeAll();
    await waitFor(() => vscode.languages.getDiagnostics(uri).length === 0, "diagnostics to clear when the notebook closes");
  });

  it("no C# Dev Kit diagnostics on cells", async () => {
    const nb = await open(notebookText(["var a = 1;", "var b = a * missing;"]));
    await waitFor(() => diagnosticsOn(cell(nb, "c2")).length > 0, "a diagnostic on c2");
    await new Promise((r) => setTimeout(r, 1000));
    for (const [uri, diagnostics] of vscode.languages.getDiagnostics()) {
      if (uri.scheme !== "vscode-notebook-cell") continue;
      for (const d of diagnostics) assert.strictEqual(d.source, "reverb", `${uri.toString()} has a diagnostic from '${d.source}': ${d.message}`);
    }
  });
});
