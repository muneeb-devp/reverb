// Collapses the input of cells with hide_code, the way the browser hides their code. Collapsing is
// a view setting, so it never touches the notebook. VS Code only does it for a notebook in an
// editor, so a notebook that isn't shown is left alone, and its cells are collapsed when it opens.
import * as vscode from "vscode";
import { hasFlag } from "./cellAttrs";
import type { ReverbCellMetadata } from "./metadata";

export function isHidden(cell: vscode.NotebookCell): boolean {
  const reverb = cell.metadata.reverb as ReverbCellMetadata | undefined;
  return cell.kind === vscode.NotebookCellKind.Code && reverb?.kind === "code" && hasFlag(reverb.attrs, "hide_code");
}

export class CellCollapse implements vscode.Disposable {
  private disposables: vscode.Disposable[] = [];
  private shown = new Set<vscode.NotebookEditor>();
  /** What was last asked for each cell. The tests read it, since VS Code doesn't say whether an input is collapsed. */
  private requested = new WeakMap<vscode.NotebookCell, boolean>();

  constructor(private readonly log: (line: string) => void = () => {}) {
    this.disposables.push(
      vscode.window.onDidChangeVisibleNotebookEditors(() => this.editorsChanged()),
      // Cells that come in with a reload, a paste or an undo, and hide_code changing under an undo or redo.
      vscode.workspace.onDidChangeNotebookDocument((e) => {
        if (e.notebook.notebookType !== "reverb") return;
        for (const change of e.contentChanges) for (const cell of change.addedCells) if (isHidden(cell)) void this.set(cell, true);
        for (const change of e.cellChanges) {
          if (!change.metadata) continue;
          const hidden = isHidden(change.cell);
          if (hidden !== (this.requested.get(change.cell) ?? false)) void this.set(change.cell, hidden);
        }
      }),
    );
    this.editorsChanged();
  }

  /** The last state asked for the cell, or undefined when none was. */
  requestedFor(cell: vscode.NotebookCell): boolean | undefined {
    return this.requested.get(cell);
  }

  /** Collapses or expands the cell's input, when an editor shows its notebook. */
  async set(cell: vscode.NotebookCell, collapsed: boolean): Promise<void> {
    const shownIn = vscode.window.visibleNotebookEditors.some((e) => e.notebook === cell.notebook);
    if (!shownIn) return;
    this.requested.set(cell, collapsed);
    try {
      await vscode.commands.executeCommand(collapsed ? "notebook.cell.collapseCellInput" : "notebook.cell.expandCellInput", {
        ranges: [{ start: cell.index, end: cell.index + 1 }],
        document: cell.notebook.uri,
      });
    } catch (e) {
      this.log(`Reverb couldn't ${collapsed ? "collapse" : "expand"} the input of cell ${cell.index + 1} in ${cell.notebook.uri.path}: ${(e as Error).message}`);
    }
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
  }

  /** Collapses the hidden cells of each editor that wasn't there before: a notebook being opened. */
  private editorsChanged() {
    const now = new Set(vscode.window.visibleNotebookEditors.filter((e) => e.notebook.notebookType === "reverb"));
    const opened = [...now].filter((e) => !this.shown.has(e));
    this.shown = now;
    for (const editor of opened) {
      for (const cell of editor.notebook.getCells()) if (isHidden(cell)) void this.set(cell, true);
    }
  }
}
