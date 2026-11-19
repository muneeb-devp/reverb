// The one NotebookController for Reverb notebooks. It keeps a NotebookSession for each open
// notebook (in a trusted folder) and restarts reverb if it stops.
import * as vscode from "vscode";
import type { ReverbDiagnostics } from "./language";
import type { Diagnostic } from "./protocol";
import type { ReverbProcess } from "./reverbProcess";
import { keyOf, NotebookSession, type SessionHost } from "./session";
import { resetWidgetLog } from "./widgets";

export const TRUST_MESSAGE = "Trust this folder to run Reverb notebooks.";
const RESTART_LIMIT = 3;
const RESTART_WINDOW_MS = 60_000;
const MAX_ERRORS = 100;

export class ReverbController implements vscode.Disposable, SessionHost {
  readonly controller: vscode.NotebookController;
  /** Error messages shown so far, newest last. The integration tests read them. */
  readonly errors: string[] = [];

  private changes = new vscode.EventEmitter<NotebookSession>();
  /** Fires when a session's statuses, kernel state, packages or lazy flag change. */
  readonly onDidChangeSession = this.changes.event;

  private sessions = new Map<string, Promise<NotebookSession | undefined>>();
  private live = new Map<string, NotebookSession>();
  private watched = new WeakSet<ReverbProcess>();
  private restarts: number[] = [];
  private current: ReverbProcess | undefined;
  private disposables: vscode.Disposable[] = [];
  private disposed = false;
  private trustOverride = false;

  constructor(
    private getProcess: () => Promise<ReverbProcess | undefined>,
    private output: (line: string) => void,
    private diagnostics: ReverbDiagnostics,
  ) {
    this.controller = vscode.notebooks.createNotebookController("reverb", "reverb", "Reverb");
    this.controller.supportedLanguages = ["reverb-csharp", "markdown"];
    this.controller.supportsExecutionOrder = true;
    this.controller.executeHandler = (cells, notebook) => this.execute(cells, notebook);
    this.controller.interruptHandler = async (notebook) => (await this.sessions.get(key(notebook)))?.interrupt();
    this.disposables.push(
      this.controller,
      vscode.workspace.onDidOpenNotebookDocument((doc) => this.open(doc)),
      vscode.workspace.onDidCloseNotebookDocument((doc) => this.close(doc)),
      vscode.workspace.onDidRenameFiles((e) => this.renamed(e.files)),
      vscode.workspace.onDidGrantWorkspaceTrust(() => this.trustGranted()),
    );
    vscode.workspace.notebookDocuments.forEach((doc) => this.open(doc));
  }

  /** The session for an open notebook, once it has been opened in reverb. */
  session(uri: vscode.Uri): NotebookSession | undefined {
    return this.live.get(uri.toString());
  }

  /** Notebooks that were open while the folder was restricted get their sessions now. */
  trustGranted(): void {
    resetWidgetLog();
    vscode.workspace.notebookDocuments.forEach((doc) => this.open(doc));
  }

  /** For the integration tests, which can't grant trust to a window: act as if the folder were trusted. */
  simulateTrust(): void {
    this.trustOverride = true;
    this.trustGranted();
  }

  private get trusted(): boolean {
    return this.trustOverride || vscode.workspace.isTrusted;
  }

  /** The reverb process the sessions use now. */
  get process(): ReverbProcess | undefined {
    return this.current;
  }

  createExecution(cell: vscode.NotebookCell): vscode.NotebookCellExecution {
    return this.controller.createNotebookCellExecution(cell);
  }

  showError(message: string, ...actions: string[]): Thenable<string | undefined> {
    this.errors.push(message);
    if (this.errors.length > MAX_ERRORS) this.errors.splice(0, this.errors.length - MAX_ERRORS);
    this.output(message);
    return vscode.window.showErrorMessage(message, ...actions);
  }

  log(line: string): void {
    this.output(line);
  }

  stateChanged(session: NotebookSession): void {
    this.changes.fire(session);
  }

  setDiagnostics(session: NotebookSession, cellId: string | undefined, diagnostics: Diagnostic[]): void {
    this.diagnostics.set(session, cellId, diagnostics);
  }

  clearDiagnostics(session: NotebookSession, cellId?: string): void {
    this.diagnostics.clear(session, cellId);
  }

  lost(session: NotebookSession, message: string): void {
    void this.showError(message);
    this.drop(session);
  }

  /**
   * The session for a notebook a command or a run needs, or undefined after saying why there
   * isn't one: the folder isn't trusted, the notebook has no file, or Reverb couldn't open it.
   */
  async requireSession(notebook: vscode.NotebookDocument): Promise<NotebookSession | undefined> {
    if (!this.trusted) {
      const trust = "Manage Workspace Trust";
      void this.showError(TRUST_MESSAGE, trust).then((choice) => {
        if (choice === trust) void vscode.commands.executeCommand("workbench.trust.manage");
      });
      return undefined;
    }
    if (notebook.uri.scheme !== "file") {
      void this.showError(`Save ${baseName(notebook)} to a file to run its cells.`);
      return undefined;
    }
    return this.sessions.get(key(notebook)) ?? this.open(notebook);
  }

  dispose(): void {
    this.disposed = true;
    for (const s of this.sessions.values()) void s.then((session) => session?.dispose());
    this.sessions.clear();
    this.live.clear();
    for (const d of this.disposables) d.dispose();
    this.changes.dispose();
  }

  // VS Code calls this for the cells the user ran. The engine decides what actually runs and
  // reports it with statuses, which start and end the executions, so nothing is ended here.
  private async execute(cells: vscode.NotebookCell[], notebook: vscode.NotebookDocument): Promise<void> {
    const session = await this.requireSession(notebook);
    if (!session) return;
    const asked = new Set(cells.map((c) => c.index));
    const code = notebook.getCells().filter((c) => c.kind === vscode.NotebookCellKind.Code);
    if (code.length > 0 && code.every((c) => asked.has(c.index))) {
      session.runAll();
      return;
    }
    const ids: string[] = [];
    for (const cell of cells) {
      const id = keyOf(cell);
      if (id) ids.push(id);
      else void this.showError(`Cell ${cell.index + 1} in ${baseName(notebook)} doesn't have a Reverb id yet, so it didn't run. Wait a moment, then run it again.`);
    }
    if (ids.length > 0) session.run(ids);
  }

  private open(doc: vscode.NotebookDocument): Promise<NotebookSession | undefined> | undefined {
    // Only files on disk: a notebook diff or a git version has no folder for a kernel to run in.
    if (this.disposed || doc.notebookType !== "reverb" || doc.uri.scheme !== "file" || !this.trusted) return undefined;
    const k = key(doc);
    const existing = this.sessions.get(k);
    if (existing) return existing;
    const opening: Promise<NotebookSession | undefined> = this.start(doc).then((session) => {
      const current = this.sessions.get(k) === opening;
      if (session && current) this.live.set(k, session);
      else session?.dispose(); // closed while it was opening
      // Running a cell later tries again.
      if (!session && current) this.sessions.delete(k);
      return current ? session : undefined;
    });
    this.sessions.set(k, opening);
    return opening;
  }

  private async start(doc: vscode.NotebookDocument): Promise<NotebookSession | undefined> {
    const proc = await this.getProcess();
    if (!proc) return undefined;
    this.watch(proc);
    try {
      return await NotebookSession.open(doc, proc, this);
    } catch (e) {
      void this.showError(`Reverb couldn't open ${baseName(doc)}, so its cells can't run: ${(e as Error).message}`);
      return undefined;
    }
  }

  /**
   * A notebook's file, or a folder it's in, was moved. Its kernel was started for the old path, so
   * the session ends there (its executions fail) and the notebook at the new path opens one. VS
   * Code also closes the old notebook and opens the new one, which then finds this already done.
   */
  private renamed(files: readonly { oldUri: vscode.Uri; newUri: vscode.Uri }[]) {
    for (const { oldUri, newUri } of files) {
      for (const k of [...this.sessions.keys()]) {
        if (!within(vscode.Uri.parse(k), oldUri)) continue;
        // Now, while VS Code still has the notebook at the old path.
        this.live.get(k)?.endAll();
        this.closeKey(k);
      }
      for (const doc of vscode.workspace.notebookDocuments) {
        if (!doc.isClosed && within(doc.uri, newUri)) this.open(doc);
      }
    }
  }

  private close(doc: vscode.NotebookDocument) {
    this.closeKey(key(doc));
  }

  private closeKey(k: string) {
    const s = this.sessions.get(k);
    if (!s) return;
    this.sessions.delete(k);
    this.live.delete(k);
    void s.then((session) => session?.dispose());
  }

  private watch(proc: ReverbProcess) {
    this.current = proc;
    if (this.watched.has(proc)) return;
    this.watched.add(proc);
    proc.onExit((code) => void this.restart(proc, code));
  }

  // Reverb stopped without being asked to. Its kernels went with it, so every execution has
  // failed and every value is gone. Start a new one and reopen the notebooks, as after a restart.
  private async restart(proc: ReverbProcess, code: number | null) {
    if (this.disposed) return;
    if (this.current === proc) this.current = undefined;
    const sessions = (await Promise.all(this.sessions.values())).filter((s): s is NotebookSession => !!s);
    for (const s of sessions) s.detach();
    if (sessions.length === 0) return;

    const now = Date.now();
    this.restarts = this.restarts.filter((t) => now - t < RESTART_WINDOW_MS);
    const why = `Reverb stopped${code === null ? "" : ` (exit code ${code})`}`;
    if (this.restarts.length >= RESTART_LIMIT) {
      sessions.forEach((s) => this.drop(s));
      void this.showError(
        `${why} ${RESTART_LIMIT} times in a minute, so it wasn't started again. Check the Reverb output panel for the reason. Running a cell starts it again.`,
      );
      return;
    }
    this.restarts.push(now);
    void this.showError(`${why}, so running cells were stopped and their values are lost. Reverb was started again: run cells to get their values back.`);

    const next = await this.getProcess();
    if (this.disposed) return;
    if (!next) {
      // getProcess has said why. The next run tries again.
      sessions.forEach((s) => this.drop(s));
      return;
    }
    this.watch(next);
    for (const s of sessions) {
      if (this.live.get(key(s.doc)) !== s) continue; // closed meanwhile
      s.attach(next).catch((e) => {
        this.drop(s);
        void this.showError(`Reverb couldn't reopen ${baseName(s.doc)}: ${(e as Error).message} Running a cell tries again.`);
      });
    }
  }

  /** Forgets a session that has no reverb process, so the next run opens the notebook again. */
  private drop(session: NotebookSession) {
    const k = key(session.doc);
    if (this.live.get(k) === session) {
      this.live.delete(k);
      this.sessions.delete(k);
    }
    session.dispose();
  }
}

function key(doc: vscode.NotebookDocument): string {
  return doc.uri.toString();
}

/** Whether `uri` is `target` or something inside it. */
function within(uri: vscode.Uri, target: vscode.Uri): boolean {
  if (uri.scheme !== target.scheme || uri.authority !== target.authority) return false;
  const folder = target.path.endsWith("/") ? target.path : `${target.path}/`;
  return uri.path === target.path || uri.path.startsWith(folder);
}

function baseName(doc: vscode.NotebookDocument): string {
  const p = doc.uri.path;
  return p.slice(p.lastIndexOf("/") + 1);
}
