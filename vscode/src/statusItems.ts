// The status bar items: one under each code cell and one for the active notebook's kernel.
import * as vscode from "vscode";
import { nameOf } from "./cellAttrs";
import type { ReverbController } from "./controller";
import { dependenciesOf, dependencyItems } from "./dependencies";
import { cellStatusText, kernelStatusText } from "./statusBar";

export class ReverbStatusItems implements vscode.Disposable {
  private cellChanged = new vscode.EventEmitter<void>();
  private kernelItem = vscode.window.createStatusBarItem("reverb.kernel", vscode.StatusBarAlignment.Left, 100);
  private disposables: vscode.Disposable[] = [];

  constructor(private controller: ReverbController) {
    this.kernelItem.name = "Reverb kernel";
    this.disposables.push(
      this.cellChanged,
      this.kernelItem,
      vscode.notebooks.registerNotebookCellStatusBarItemProvider("reverb", {
        onDidChangeCellStatusBarItems: this.cellChanged.event,
        provideCellStatusBarItems: (cell) => {
          const items: vscode.NotebookCellStatusBarItem[] = [];
          const name = this.nameText(cell);
          if (name) items.push({ text: name, tooltip: "Cell name", alignment: vscode.NotebookCellStatusBarAlignment.Left, priority: 10 });
          for (const d of this.dependencyTexts(cell)) {
            items.push({
              ...d,
              alignment: vscode.NotebookCellStatusBarAlignment.Left,
              priority: 5,
              command: { command: "reverb.showDependencies", title: "Show Dependencies", arguments: [cell] },
            });
          }
          const text = this.cellText(cell);
          if (text) items.push({ text, alignment: vscode.NotebookCellStatusBarAlignment.Left });
          return items;
        },
      }),
      vscode.workspace.onDidChangeNotebookDocument((e) => {
        if (e.notebook.notebookType === "reverb" && e.cellChanges.some((c) => c.metadata)) this.cellChanged.fire();
      }),
      controller.onDidChangeSession(() => this.refresh()),
      vscode.window.onDidChangeActiveNotebookEditor(() => this.refreshKernel()),
      vscode.workspace.onDidGrantWorkspaceTrust(() => this.refresh()),
    );
    this.refreshKernel();
  }

  /** The text under a cell, or undefined when it has none. */
  cellText(cell: vscode.NotebookCell): string | undefined {
    if (cell.kind !== vscode.NotebookCellKind.Code || cell.metadata.reverb?.kind !== "code") return undefined;
    const id = cell.metadata.reverb?.id as string | undefined;
    const session = this.controller.session(cell.notebook.uri);
    if (!id || !session) return undefined;
    return cellStatusText(session.statusOf(id), session.messageOf(id));
  }

  /** The cell's name, shown under it as its own item. It's file content, so it shows without a session. */
  nameText(cell: vscode.NotebookCell): string | undefined {
    if (cell.kind !== vscode.NotebookCellKind.Code || cell.metadata.reverb?.kind !== "code") return undefined;
    return nameOf(cell.metadata.reverb?.attrs);
  }

  /** The "uses" and "used by" items under a cell. */
  dependencyTexts(cell: vscode.NotebookCell): { text: string; tooltip: string }[] {
    if (cell.kind !== vscode.NotebookCellKind.Code || cell.metadata.reverb?.kind !== "code") return [];
    const id = cell.metadata.reverb?.id as string | undefined;
    const session = this.controller.session(cell.notebook.uri);
    if (!id || !session) return [];
    return dependencyItems(dependenciesOf(id, session.edges), (other) => cellLabel(cell.notebook, other));
  }

  /** The kernel item as shown, or undefined while no Reverb notebook is active. */
  kernelText(): { text: string; tooltip: string } | undefined {
    const doc = vscode.window.activeNotebookEditor?.notebook;
    if (doc?.notebookType !== "reverb") return undefined;
    const session = this.controller.session(doc.uri);
    return kernelStatusText(session?.kernelState, session?.kernelMessage, vscode.workspace.isTrusted);
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
  }

  private refresh() {
    this.cellChanged.fire();
    this.refreshKernel();
  }

  private refreshKernel() {
    const shown = this.kernelText();
    if (!shown) {
      this.kernelItem.hide();
      return;
    }
    this.kernelItem.text = shown.text;
    this.kernelItem.tooltip = shown.tooltip;
    this.kernelItem.show();
  }
}

/** A cell's name, else "cell N" for its 1-based position in the notebook. */
export function cellLabel(notebook: vscode.NotebookDocument, id: string): string {
  const cell = notebook.getCells().find((c) => c.metadata.reverb?.id === id);
  if (!cell) return id;
  return nameOf(cell.metadata.reverb?.attrs) ?? `cell ${cell.index + 1}`;
}
