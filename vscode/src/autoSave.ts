// Saves Reverb notebooks the way the browser editor does: a second after typing stops, and at
// once after cells are added, removed or moved, or the packages or lazy mode change. Keeping the
// file saved is also what lets VS Code reload the notebook when the file changes on disk.
import * as vscode from "vscode";
import { saveDelay, TYPING_SAVE_DELAY_MS } from "./notebookChanges";
import { summarize } from "./session";

interface Pending {
  timer: NodeJS.Timeout;
  delay: number;
}

export class AutoSave implements vscode.Disposable {
  private pending = new Map<vscode.NotebookDocument, Pending>();
  private listeners: vscode.Disposable[];

  constructor(private log: (line: string) => void) {
    this.listeners = [
      vscode.workspace.onDidChangeNotebookDocument((e) => this.changed(e.notebook, saveDelay(summarize(e)))),
      vscode.workspace.onDidChangeTextDocument((e) => {
        if (e.document.uri.scheme !== "vscode-notebook-cell" || e.contentChanges.length === 0) return;
        const notebook = vscode.workspace.notebookDocuments.find((nb) => nb.getCells().some((c) => c.document === e.document));
        if (notebook) this.changed(notebook, TYPING_SAVE_DELAY_MS);
      }),
      vscode.workspace.onDidCloseNotebookDocument((nb) => this.cancel(nb)),
    ];
  }

  dispose(): void {
    for (const nb of [...this.pending.keys()]) this.cancel(nb);
    for (const l of this.listeners) l.dispose();
  }

  private changed(nb: vscode.NotebookDocument, delay: number | undefined) {
    if (delay === undefined || nb.notebookType !== "reverb" || nb.uri.scheme !== "file") return;
    if (!vscode.workspace.getConfiguration("reverb").get<boolean>("autoSave", true)) return;
    const pending = this.pending.get(nb);
    // Typing right after a structural change doesn't hold that save back.
    if (pending && pending.delay < delay) return;
    this.cancel(nb);
    const timer = setTimeout(() => {
      this.pending.delete(nb);
      void this.save(nb);
    }, delay);
    this.pending.set(nb, { timer, delay });
  }

  private cancel(nb: vscode.NotebookDocument) {
    clearTimeout(this.pending.get(nb)?.timer);
    this.pending.delete(nb);
  }

  // VS Code doesn't write a notebook that has no unsaved changes, so opening it, a reload from disk
  // and undone edits don't save. isDirty isn't checked here: right after a change it can still be
  // the value from before it.
  private async save(nb: vscode.NotebookDocument) {
    if (nb.isClosed) return;
    try {
      // save() is also false when there was nothing to save. VS Code tells the user itself when a
      // save fails, for example when the file changed on disk, so this only goes to the log.
      if (!(await nb.save()) && nb.isDirty) this.log(`Autosave of ${nb.uri.fsPath} didn't save the file.`);
    } catch (e) {
      this.log(`Autosave of ${nb.uri.fsPath} failed: ${(e as Error).message}`);
    }
  }
}
