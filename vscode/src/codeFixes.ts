// Quick fixes for the diagnostics Reverb can fix itself: a using, type or statement in the wrong
// cell, and an anonymous type passed between cells. The server works out the fixed cells and
// changes nothing; the edit is made here, so it syncs, saves and undoes like any other edit.
import * as vscode from "vscode";
import type { ReverbController } from "./controller";
import { planFixEdits } from "./fixEdits";
import { targetOf, type ReverbDiagnostics } from "./language";
import type { ReverbCellMetadata } from "./metadata";
import type { Diagnostic, PartCell } from "./protocol";

export const APPLY_FIX = "reverb.applyFix";

export function registerCodeFixes(controller: ReverbController, diagnostics: ReverbDiagnostics): vscode.Disposable[] {
  const provider: vscode.CodeActionProvider = {
    provideCodeActions(document, _range, context) {
      const target = targetOf(document, (uri) => controller.session(uri));
      if (!target) return undefined;
      const actions: vscode.CodeAction[] = [];
      for (const shown of context.diagnostics) {
        if (shown.source !== "reverb") continue;
        const d = diagnostics.find(target.session, target.cellId, shown);
        if (!d?.fix) continue;
        const action = new vscode.CodeAction(d.fix, vscode.CodeActionKind.QuickFix);
        action.diagnostics = [shown];
        action.isPreferred = true;
        action.command = { command: APPLY_FIX, title: d.fix, arguments: [target.session.doc.uri.toString(), target.cellId, d] };
        actions.push(action);
      }
      return actions;
    },
  };
  return [
    vscode.languages.registerCodeActionsProvider({ language: "reverb-csharp" }, provider, { providedCodeActionKinds: [vscode.CodeActionKind.QuickFix] }),
    vscode.commands.registerCommand(APPLY_FIX, (notebook: string, cellId: string, diagnostic: Diagnostic) => applyFix(controller, notebook, cellId, diagnostic)),
  ];
}

async function applyFix(controller: ReverbController, notebook: string, cellId: string, diagnostic: Diagnostic): Promise<void> {
  const doc = vscode.workspace.notebookDocuments.find((d) => d.uri.toString() === notebook);
  const session = doc && controller.session(doc.uri);
  if (!doc || !session) {
    void controller.showError("The notebook isn't open in Reverb any more, so the fix wasn't applied. Open it, then try the fix again.");
    return;
  }
  const name = doc.uri.path.slice(doc.uri.path.lastIndexOf("/") + 1);
  const at = session.cell(cellId)?.index;
  const fail = (why: string) => void controller.showError(`The fix for ${at === undefined ? name : `cell ${at + 1} in ${name}`} wasn't applied. ${why}`);

  const asked = JSON.stringify(session.cells);
  const answer = await session.requestCodeFix(cellId, diagnostic);
  if (!answer.cells) return fail(forVsCode(answer.message) ?? "Reverb sent no cells for it. Try the fix again.");
  const current = session.cells;
  // The server refuses a fix when a sync reached it first, but typing that wasn't synced yet would be lost.
  if (JSON.stringify(current) !== asked) return fail("The notebook changed while Reverb was working out the fix. Try the fix again.");
  if (current.some((c) => c.kind !== "setup" && c.kind !== "types" && !c.id)) {
    return fail("Some cells don't have a Reverb id yet. Wait a moment, then try the fix again.");
  }

  const plan = planFixEdits(current.map((c) => ({ kind: c.kind, id: c.id ?? undefined, source: c.source })), answer.cells);
  if (plan.replace.length === 0 && plan.insert.length === 0) return;
  const cells = doc.getCells();
  const edit = new vscode.WorkspaceEdit();
  for (const r of plan.replace) {
    const text = cells[r.index].document;
    edit.replace(text.uri, new vscode.Range(text.positionAt(0), text.positionAt(text.getText().length)), r.source);
  }
  const inserts: { index: number; cells: vscode.NotebookCellData[] }[] = [];
  for (const i of plan.insert) {
    const last = inserts[inserts.length - 1];
    if (last?.index === i.index) last.cells.push(toCellData(i.cell));
    else inserts.push({ index: i.index, cells: [toCellData(i.cell)] });
  }
  if (inserts.length > 0) edit.set(doc.uri, inserts.map((i) => vscode.NotebookEdit.insertCells(i.index, i.cells)));
  if (!(await vscode.workspace.applyEdit(edit))) return fail("VS Code didn't accept the edit. Try the fix again.");

  // As in the browser, every cell the fix changed goes to the kernel. Running a setup or types cell
  // only hands it its new text, which the code cells need before they run.
  const changed = [...plan.replace.map((r) => current[r.index]), ...plan.insert.map((i) => i.cell)];
  const key = (c: PartCell) => (c.kind === "setup" || c.kind === "types" ? c.kind : c.id ?? undefined);
  const run = [
    ...changed.filter((c) => c.kind === "setup" || c.kind === "types"),
    ...changed.filter((c) => c.kind === "code"),
  ]
    .map(key)
    .filter((k): k is string => !!k);
  // In lazy mode the browser only marks changed cells stale, so here they wait for a run too.
  if (run.length > 0 && !session.lazy) session.run(run);
}

/** The server tells browser users to add a setup cell by hand. Opening a file without one adds it here. */
function forVsCode(message: string | undefined): string | undefined {
  return message?.replace("Add a '// %% setup' cell at the top, then", "Save the notebook and open it again, which adds a setup cell, then");
}

/** A cell the fix added, with the Reverb metadata the serializer would give it. */
function toCellData(cell: PartCell): vscode.NotebookCellData {
  const markup = cell.kind === "markdown";
  const data = new vscode.NotebookCellData(
    markup ? vscode.NotebookCellKind.Markup : vscode.NotebookCellKind.Code,
    cell.source,
    markup ? "markdown" : "reverb-csharp",
  );
  const reverb: ReverbCellMetadata = { kind: cell.kind, attrs: cell.attributes ?? [] };
  if (cell.id) reverb.id = cell.id;
  data.metadata = { reverb };
  return data;
}
