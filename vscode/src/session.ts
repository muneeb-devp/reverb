// One open Reverb notebook: its host in the reverb process, the cell list sent to it, and the
// VS Code executions and outputs that mirror what the engine does.
import * as vscode from "vscode";
import { ExecutionTracker, type ExecAction } from "./executions";
import { cellsToParts, readNotebookMetadata, type PlainCell, type ReverbCellMetadata } from "./metadata";
import { correctionOrder, isReload, notebookLost, type ChangeSummary } from "./notebookChanges";
import { appendStream, errorItem, toOutputItems } from "./outputs";
import type { CellStatus, Diagnostic, FormatSerialized, KernelState, NotebookPartsData, Output, Package, PartCell, PreambleLine, ServerMessage, WidgetKind } from "./protocol";
import type { ReverbProcess } from "./reverbProcess";
import { decodeNotebook } from "./serializer";

const SYNC_DELAY_MS = 150;
const STREAM_BATCH_MS = 50;

/** What a session needs from the controller. */
export interface SessionHost {
  createExecution(cell: vscode.NotebookCell): vscode.NotebookCellExecution;
  showError(message: string): void;
  log(line: string): void;
  /** A status, the kernel state, the packages or the lazy flag of the session changed. */
  stateChanged(session: NotebookSession): void;
  /** The diagnostics of a cell (by key), or of the notebook when `cellId` is undefined, are now these. */
  setDiagnostics(session: NotebookSession, cellId: string | undefined, diagnostics: Diagnostic[]): void;
  /** A cell was deleted, or the session ended: its diagnostics go. With no cell, every diagnostic of the notebook goes. */
  clearDiagnostics(session: NotebookSession, cellId?: string): void;
  /** Reverb has no host for the notebook any more. Say why, once, and forget the session so the next run opens it again. */
  lost(session: NotebookSession, message: string): void;
}

export type LanguageRequest = "lang.completions" | "lang.hover" | "lang.signatureHelp";
export type LanguageAnswer = Extract<ServerMessage, { type: "lang.completionsResult" | "lang.hoverResult" | "lang.signatureHelpResult" }>;

const LANGUAGE_TIMEOUT_MS = 5_000;

interface CellOutputs {
  /** The cell's outputs as the server has them. */
  outputs: Output[];
  /** What was written to the open execution, index for index with `outputs`. */
  shown?: vscode.NotebookCellOutput[];
  /** `outputs` changed while there was no started execution to write them to. */
  dirty: boolean;
  timer?: NodeJS.Timeout;
}

export class NotebookSession implements vscode.Disposable {
  readonly path: string;
  kernelState: KernelState | undefined;
  kernelMessage: string | undefined;
  /** The notebook's packages (Reverb.Runtime excluded) as the last snapshot listed them. Undefined until one arrives. */
  packages: Package[] | undefined;
  lazy: boolean | undefined;

  private proc: ReverbProcess | undefined;
  private loggedNoProcess = false;
  private handler: vscode.Disposable | undefined;
  private opened = false;
  private disposed = false;
  private syncTimer: NodeJS.Timeout | undefined;
  /**
   * The cell list the server has, as JSON. A list equal to it isn't sent, which is what keeps the
   * session's own edits (corrections, the preamble) from being synced back.
   */
  private lastSent: string | undefined;
  /** Reloads waiting for the file's text. Syncs wait for them, so they can't arrive first. */
  private reloads = 0;
  private syncs = 0;
  private tracker = new ExecutionTracker();
  private executions = new Map<string, vscode.NotebookCellExecution>();
  private statuses = new Map<string, CellStatus>();
  private messages = new Map<string, string>();
  private writingPreamble: string | undefined;
  /** The preamble in the server's copy of the notebook, from its last snapshot. */
  private serverPreamble: PreambleLine[] | undefined;
  /** Whether the user was told that packages and lazy mode can't be changed by editing the metadata. */
  private toldPreamble = false;
  private outputs = new Map<string, CellOutputs>();
  private listeners: vscode.Disposable[] = [];
  private nextLanguageId = 1;
  private languageRequests = new Map<number, (m: LanguageAnswer | undefined) => void>();

  private constructor(
    readonly doc: vscode.NotebookDocument,
    private host: SessionHost,
  ) {
    this.path = doc.uri.fsPath;
    // Executions change outputs and execution summaries, which also fire these events. Only
    // text, metadata and cell list changes go to the server, and an unchanged list isn't resent.
    this.listeners.push(
      vscode.workspace.onDidChangeNotebookDocument((e) => {
        if (e.notebook !== doc) return;
        const change = summarize(e);
        // isDirty still has the value from before this change, so reload() decides once it has
        // caught up. File: Revert starts dirty and ends clean.
        const reload = isReload(change, this.writingPreamble !== undefined);
        this.forgetRemoved(e.contentChanges, reload);
        if (reload) void this.reload();
        else if (change.content.length > 0 || change.text || change.cellMetadata) this.sync();
      }),
      vscode.workspace.onDidChangeTextDocument((e) => {
        if (e.document.uri.scheme === "vscode-notebook-cell" && doc.getCells().some((c) => c.document === e.document)) this.sync();
      }),
    );
  }

  /** Opens the notebook in reverb with the text VS Code has now, which may be unsaved. */
  static async open(doc: vscode.NotebookDocument, proc: ReverbProcess, host: SessionHost): Promise<NotebookSession> {
    const session = new NotebookSession(doc, host);
    try {
      await session.attach(proc);
    } catch (e) {
      session.dispose();
      throw e;
    }
    return session;
  }

  /** Opens the notebook in a (new) reverb process. Values from an earlier process are gone. */
  async attach(proc: ReverbProcess): Promise<void> {
    this.detach();
    this.proc = proc;
    this.handler = proc.onNotebookMessage(this.path, (m) => this.receive(m));
    const parts = this.parts();
    const { text } = await proc.request<FormatSerialized>({ type: "format.serialize", parts });
    if (this.disposed || this.proc !== proc) return;
    // The host gives a cell without an id the same one format.serialize wrote, but only a sync
    // brings it back to VS Code. So with such a cell, the list counts as not sent yet.
    this.lastSent = parts.cells.some((c) => !c.id) ? undefined : JSON.stringify(parts.cells);
    proc.send({ type: "notebook.open", notebook: this.path, text });
    this.opened = true;
    // Anything edited while the text was being made goes now.
    this.flush();
  }

  /** The process went away: every open execution has failed. */
  detach(): void {
    this.endAll();
    this.handler?.dispose();
    this.handler = undefined;
    this.proc = undefined;
    this.opened = false;
    this.kernelState = undefined;
    this.kernelMessage = undefined;
    this.packages = undefined;
    this.lazy = undefined;
    this.serverPreamble = undefined;
    this.statuses.clear();
    this.messages.clear();
    for (const answer of [...this.languageRequests.values()]) answer(undefined);
    this.host.clearDiagnostics(this);
    this.host.stateChanged(this);
  }

  /** Sends the cell list soon. Typing produces many changes, so they're combined. */
  sync(): void {
    clearTimeout(this.syncTimer);
    this.syncTimer = setTimeout(() => this.flush(), SYNC_DELAY_MS);
  }

  /** Sends the cell list now if it changed since it was last sent. */
  flush(): void {
    clearTimeout(this.syncTimer);
    this.syncTimer = undefined;
    // A reload flushes when it's done.
    if (!this.opened || !this.proc || this.reloads > 0) return;
    const cells = this.parts().cells;
    const json = JSON.stringify(cells);
    if (json === this.lastSent) return;
    this.lastSent = json;
    this.syncs++;
    this.proc.send({ type: "notebook.sync", notebook: this.path, cells });
  }

  /**
   * Asks the kernel's language services about a cell, with its text and offset as the editor has
   * them now. Resolves to undefined when there's no answer: the session isn't open, the request was
   * cancelled, or the server didn't answer in time (a host error doesn't say which request failed).
   */
  requestLanguage(type: LanguageRequest, cellId: string, source: string, offset: number, token?: vscode.CancellationToken): Promise<LanguageAnswer | undefined> {
    const proc = this.proc;
    if (!this.opened || !proc || token?.isCancellationRequested) return Promise.resolve(undefined);
    const requestId = this.nextLanguageId++;
    return new Promise((resolve) => {
      const done = (m: LanguageAnswer | undefined) => {
        if (!this.languageRequests.delete(requestId)) return;
        clearTimeout(timer);
        cancel?.dispose();
        resolve(m);
      };
      const timer = setTimeout(() => {
        this.host.log(`The language services didn't answer for cell '${cellId}' in ${this.name} within ${LANGUAGE_TIMEOUT_MS / 1000} s, so there are no suggestions for it.`);
        done(undefined);
      }, LANGUAGE_TIMEOUT_MS);
      const cancel = token?.onCancellationRequested(() => done(undefined));
      this.languageRequests.set(requestId, done);
      proc.send({ type, notebook: this.path, cellId, source, offset, requestId });
    });
  }

  /** Runs cells by Reverb id. The engine also runs what they depend on and what depends on them. */
  run(cellIds: string[]): void {
    this.flush();
    if (!this.canRun()) return;
    for (const id of cellIds) {
      this.queue(id);
      this.proc!.send({ type: "cell.run", notebook: this.path, cellId: id });
    }
  }

  runAll(): void {
    this.flush();
    if (!this.canRun()) return;
    for (const cell of this.doc.getCells()) {
      const id = keyOf(cell);
      if (id) this.queue(id);
    }
    this.proc!.send({ type: "notebook.runAll", notebook: this.path });
  }

  interrupt(): void {
    this.proc?.send({ type: "kernel.interrupt", notebook: this.path });
  }

  setWidget(widgetId: string, value: unknown, kind: WidgetKind): void {
    if (!this.proc) {
      if (!this.loggedNoProcess) {
        this.loggedNoProcess = true;
        this.host.log(`${this.name} has no running Reverb, so widget changes were dropped. Run a cell to start it again.`);
      }
      return;
    }
    this.loggedNoProcess = false;
    this.proc.send({ type: "widget.setValue", notebook: this.path, widgetId, value, kind });
  }

  /** The cell's status as the server last reported it. */
  statusOf(cellId: string): CellStatus | undefined {
    return this.statuses.get(cellId);
  }

  /** The server's message for the cell: why it can't run, for an upstream error. */
  messageOf(cellId: string): string | undefined {
    return this.messages.get(cellId);
  }

  /** Replaces the notebook's packages. The server rebuilds the kernel and sends the new preamble back. */
  setPackages(packages: Package[]): void {
    if (this.opened) this.proc?.send({ type: "packages.set", notebook: this.path, packages });
  }

  setLazy(lazy: boolean): void {
    if (this.opened) this.proc?.send({ type: "notebook.setLazy", notebook: this.path, lazy });
  }

  /** Ends every open execution, then starts a new kernel. Every cell is stale after it. */
  restart(): void {
    this.endAll();
    if (this.opened) this.proc?.send({ type: "kernel.restart", notebook: this.path });
  }

  /** How many notebook.sync messages were sent. The tests read it. */
  get syncsSent(): number {
    return this.syncs;
  }

  /** The cells with an execution open in VS Code. The integration tests read it. */
  get executing(): string[] {
    return [...this.executions.keys()];
  }

  /** Ends every open execution as failed. */
  endAll(): void {
    this.apply(this.tracker.endAll());
  }

  dispose(): void {
    if (this.disposed) return;
    const proc = this.proc;
    const opened = this.opened;
    clearTimeout(this.syncTimer);
    this.detach();
    this.disposed = true;
    this.host.clearDiagnostics(this);
    for (const o of this.outputs.values()) clearTimeout(o.timer);
    for (const l of this.listeners) l.dispose();
    if (opened) proc?.send({ type: "notebook.close", notebook: this.path });
  }

  private canRun(): boolean {
    if (!this.opened || !this.proc) {
      this.host.showError(`Reverb hasn't opened ${this.name} yet, so nothing ran. Wait a moment and run it again.`);
      return false;
    }
    if (this.kernelState === "dead") {
      this.host.showError(`The kernel for ${this.name} isn't running, so nothing ran. ${this.kernelMessage ?? ""}`.trim());
      return false;
    }
    return true;
  }

  /**
   * Shows a cell VS Code asked for as queued straight away. Only code cells get statuses from the
   * engine (setup and types don't), so only they get an execution that a status will end.
   */
  private queue(cellId: string) {
    const cell = this.cell(cellId);
    if (cell?.metadata.reverb?.kind !== "code") return;
    this.apply(this.tracker.onStatus(cellId, "queued", 0, Date.now()));
  }

  private receive(m: ServerMessage) {
    switch (m.type) {
      case "notebook.snapshot":
        this.setKernel(m.kernel.state, m.kernel.message);
        this.packages = m.packages;
        this.lazy = m.lazy;
        for (const c of m.cells) {
          this.setStatus(c.id, c.status, c.message);
          this.setOutputs(c.id, c.outputs);
          this.host.setDiagnostics(this, c.id, c.diagnostics ?? []);
        }
        this.host.setDiagnostics(this, undefined, m.notebookDiagnostics ?? []);
        if (m.preamble) this.serverPreamble = m.preamble;
        this.writePreamble(m.preamble);
        this.host.stateChanged(this);
        break;
      case "cell.status":
        this.setStatus(m.cellId, m.status, m.message);
        this.host.stateChanged(this);
        this.apply(this.tracker.onStatus(m.cellId, m.status, m.runs ?? 0, Date.now()));
        break;
      case "cell.diagnostics":
        this.host.setDiagnostics(this, m.cellId, m.diagnostics);
        break;
      case "lang.completionsResult":
      case "lang.hoverResult":
      case "lang.signatureHelpResult":
        this.languageRequests.get(m.requestId)?.(m);
        break;
      case "cell.outputs":
        this.setOutputs(m.cellId, m.outputs);
        break;
      case "cell.stream":
        this.addStream(m.cellId, m.stream, m.text);
        break;
      case "kernel.status":
        this.setKernel(m.state, m.message);
        break;
      case "notebook.cells":
        this.applyCorrection(m.partCells);
        break;
      case "error":
        if (notebookLost(m.message)) this.lose(m.message);
        else this.host.showError(m.message);
        break;
    }
  }

  /**
   * VS Code read the file again because it changed on disk. The server gets the file's text, not
   * the cells, so the lines above the cells (packages, lazy mode) reach it too.
   */
  private async reload() {
    const proc = this.proc;
    // A notebook that isn't open in reverb yet is sent as VS Code has it when it opens.
    if (!this.opened || !proc) return;
    // The cells VS Code read from the file, which the server will read from the same text.
    const cells = JSON.stringify(this.parts().cells);
    this.reloads++;
    let text: string | undefined;
    try {
      text = decodeNotebook(await vscode.workspace.fs.readFile(this.doc.uri));
    } catch (e) {
      this.host.log(`Couldn't read ${this.name} after it changed on disk, so its cells were sent instead: ${(e as Error).message}`);
    } finally {
      this.reloads--;
    }
    // A clean notebook is what its file has. One that is dirty by now was edited, and is synced.
    if (text !== undefined && !this.doc.isDirty && this.proc === proc && !this.disposed) {
      this.lastSent = cells;
      proc.send({ type: "notebook.reload", notebook: this.path, text });
    } else {
      this.keepServerPreamble();
    }
    this.flush();
  }

  /**
   * The notebook's metadata changed without a reload: an undo of a package or lazy mode change,
   * say. A sync only carries the cells, so the server would keep its preamble and the next save
   * would write the other one. The server's preamble goes back into the notebook instead.
   */
  private keepServerPreamble() {
    const server = this.serverPreamble;
    if (!server || this.disposed || !this.opened) return;
    if (JSON.stringify(readNotebookMetadata(this.doc.metadata).preamble) === JSON.stringify(server)) return;
    this.writePreamble(server);
    if (this.toldPreamble) return;
    this.toldPreamble = true;
    this.host.showError(
      `The lines above the cells of ${this.name} were changed outside Reverb, so they were put back. To change packages or lazy mode, use Reverb: Add Package, Remove Package or Toggle Lazy Mode.`,
    );
  }

  /** Reverb has no host for this notebook: it failed to open, or was closed there. */
  private lose(serverMessage: string) {
    if (!this.opened) return;
    this.opened = false;
    this.endAll();
    const message = / couldn't be opened: /.test(serverMessage)
      ? `${serverMessage} Run a cell to try again.`
      : `${this.name} isn't open in Reverb any more, so its kernel and values are gone. Run a cell to open it again.`;
    this.host.lost(this, message);
  }

  /**
   * The server fixed the cell list it was sent: new ids for pasted or duplicated cells, setup
   * first and types last. Writing the fixes into the notebook keeps VS Code, the kernel and the
   * file in step. Cells are matched by position, and only their Reverb metadata changes, so text
   * typed since then is kept.
   */
  private applyCorrection(corrected: PartCell[]) {
    if (this.disposed) return;
    const cells = this.doc.getCells();
    const order = correctionOrder(this.parts().cells.map((c) => c.kind), corrected);
    if (!order) {
      // Cells were added or removed since that list was sent. Its sync brings a new correction.
      this.lastSent = undefined;
      this.sync();
      return;
    }
    const moves: vscode.NotebookEdit[] = [];
    const updates: vscode.NotebookEdit[] = [];
    const fixed: PlainCell[] = [];
    order.forEach((from, to) => {
      const cell = cells[from];
      const fix = corrected[to];
      const before = cell.metadata.reverb as ReverbCellMetadata | undefined;
      const reverb: ReverbCellMetadata = { ...before, kind: fix.kind, attrs: fix.attributes ?? [] };
      if (fix.id) reverb.id = fix.id;
      else delete reverb.id;
      const metadata = { ...cell.metadata, reverb };
      fixed.push({ ...toPlainCell(cell), source: fix.source, metadata: { reverb } });
      if (from !== to && (fix.kind === "setup" || fix.kind === "types")) {
        // There's no move edit, so it's taken out and put back with everything it had.
        const data = new vscode.NotebookCellData(cell.kind, cell.document.getText(), cell.document.languageId);
        data.metadata = metadata;
        data.outputs = [...cell.outputs];
        data.executionSummary = cell.executionSummary;
        moves.push(vscode.NotebookEdit.deleteCells(new vscode.NotebookRange(from, from + 1)));
        moves.push(vscode.NotebookEdit.insertCells(fix.kind === "setup" ? 0 : cells.length, [data]));
      } else if (JSON.stringify(before) !== JSON.stringify(reverb)) {
        updates.push(vscode.NotebookEdit.updateCellMetadata(from, metadata));
      }
    });
    if (moves.length === 0 && updates.length === 0) return;
    // This is the list the server has now, so the edit below doesn't go back to it. Text typed
    // since the list was sent differs from it and is synced.
    this.lastSent = JSON.stringify(cellsToParts(fixed, readNotebookMetadata(this.doc.metadata)).cells);
    // Every index is one in the notebook as it is now: VS Code applies the edits from the end of
    // the notebook back. Where a metadata edit and a delete end at the same index, the one later
    // in the list goes first, so the metadata edits come last.
    const edit = new vscode.WorkspaceEdit();
    edit.set(this.doc.uri, [...moves, ...updates]);
    void vscode.workspace.applyEdit(edit).then(
      (ok) => {
        if (ok) return;
        this.lastSent = undefined;
        this.sync();
      },
      (e) => this.host.log(`Couldn't write the corrected cells of ${this.name}: ${(e as Error).message}`),
    );
  }

  private setKernel(state: KernelState, message: string | undefined) {
    const before = this.kernelState;
    this.kernelState = state;
    this.kernelMessage = message;
    this.host.stateChanged(this);
    // A dead kernel, or one starting again after it was up (a restart), won't report on the
    // cells it had. A first start while building packages keeps the cells waiting for it.
    const restarted = (state === "starting" || state === "building") && (before === "ready" || before === "busy");
    if (state === "dead" || restarted) this.endAll();
    if (state === "dead" && message && before !== "dead") this.host.showError(`${this.name}: ${message}`);
  }

  private setStatus(cellId: string, status: CellStatus, message: string | undefined) {
    this.statuses.set(cellId, status);
    if (message) this.messages.set(cellId, message);
    else this.messages.delete(cellId);
  }

  /**
   * Package and lazy changes happen in the server's copy of the notebook, which has the new
   * directive or header line. Writing its preamble to the notebook's metadata is what puts
   * the change in the file on the next save, and makes the notebook dirty.
   */
  private writePreamble(preamble: PreambleLine[] | undefined) {
    if (!preamble || this.disposed) return;
    const metadata = readNotebookMetadata(this.doc.metadata);
    const json = JSON.stringify(preamble);
    if (json === JSON.stringify(metadata.preamble) || json === this.writingPreamble) return;
    this.writingPreamble = json;
    const edit = new vscode.WorkspaceEdit();
    edit.set(this.doc.uri, [vscode.NotebookEdit.updateNotebookMetadata({ ...this.doc.metadata, reverb: { ...metadata, preamble } })]);
    void vscode.workspace.applyEdit(edit).then(
      (ok) => {
        this.writingPreamble = undefined;
        if (!ok) this.host.showError(`Reverb couldn't record the change in ${this.name}, so it won't be in the file when you save. Run the command again.`);
      },
      (e) => {
        this.writingPreamble = undefined;
        this.host.log(`Couldn't write the preamble of ${this.name}: ${(e as Error).message}`);
      },
    );
  }

  private apply(actions: ExecAction[]) {
    for (const a of actions) {
      switch (a.kind) {
        case "create":
          this.create(a.cellId);
          break;
        case "start": {
          const execution = this.executions.get(a.cellId) ?? this.create(a.cellId);
          if (!execution) break;
          execution.executionOrder = a.order;
          execution.start(a.startTime);
          this.writeAll(a.cellId, execution);
          break;
        }
        case "order": {
          const execution = this.executions.get(a.cellId);
          if (execution) execution.executionOrder = a.order;
          break;
        }
        case "end": {
          const execution = this.executions.get(a.cellId);
          if (!execution) break;
          this.executions.delete(a.cellId);
          const o = this.outputsOf(a.cellId);
          if (o.shown) this.flushStream(a.cellId, execution);
          else if (o.dirty) {
            // It never started, but the engine changed its outputs (cleared them before failing, say).
            execution.start(Date.now());
            this.writeAll(a.cellId, execution);
          }
          o.shown = undefined;
          execution.end(a.success, a.endTime);
          break;
        }
      }
    }
  }

  /**
   * The server drops a deleted cell without a status, and VS Code doesn't cancel a pending one, so
   * its execution is ended here. A moved cell keeps its key, and its execution. A reload replaces
   * the cells that changed with new ones under the same key, so their executions end too.
   */
  private forgetRemoved(changes: readonly vscode.NotebookDocumentContentChange[], replaced: boolean) {
    const remaining = new Set(this.doc.getCells().map(keyOf));
    for (const change of changes) {
      for (const cell of change.removedCells) {
        const id = keyOf(cell);
        if (!id || (remaining.has(id) && !replaced)) continue;
        // Nothing is written to a deleted cell, so its outputs go before its execution ends.
        clearTimeout(this.outputs.get(id)?.timer);
        this.outputs.delete(id);
        this.statuses.delete(id);
        this.messages.delete(id);
        this.apply(this.tracker.forget(id));
        this.outputs.delete(id);
        this.host.clearDiagnostics(this, id);
      }
    }
  }

  private create(cellId: string): vscode.NotebookCellExecution | undefined {
    const cell = this.cell(cellId);
    if (!cell) return undefined;
    try {
      const execution = this.host.createExecution(cell);
      this.executions.set(cellId, execution);
      return execution;
    } catch (e) {
      this.host.log(`Couldn't show cell '${cellId}' running in ${this.name}: ${(e as Error).message}`);
      return undefined;
    }
  }

  private outputsOf(cellId: string): CellOutputs {
    let o = this.outputs.get(cellId);
    if (!o) this.outputs.set(cellId, (o = { outputs: [], dirty: false }));
    return o;
  }

  private setOutputs(cellId: string, outputs: Output[]) {
    const o = this.outputsOf(cellId);
    // Every snapshot repeats every cell's outputs. Only a change needs writing.
    if (JSON.stringify(outputs) === JSON.stringify(o.outputs)) return;
    o.outputs = outputs;
    const execution = this.executions.get(cellId);
    if (o.shown && execution) this.writeAll(cellId, execution);
    else o.dirty = true;
  }

  // Streams can be thousands of small chunks, so they're written at most once per STREAM_BATCH_MS.
  private addStream(cellId: string, stream: "stdout" | "stderr", text: string) {
    const o = this.outputsOf(cellId);
    o.outputs = appendStream(o.outputs, stream, text);
    o.dirty = true;
    if (!o.shown) return;
    o.timer ??= setTimeout(() => {
      const execution = this.executions.get(cellId);
      if (execution) this.flushStream(cellId, execution);
    }, STREAM_BATCH_MS);
  }

  /** Writes stream text that arrived since the last write: the last output grew, or one was added. */
  private flushStream(cellId: string, execution: vscode.NotebookCellExecution) {
    const o = this.outputsOf(cellId);
    clearTimeout(o.timer);
    o.timer = undefined;
    const shown = o.shown;
    if (!shown || !o.dirty) return;
    const n = o.outputs.length;
    const last = o.outputs[n - 1];
    if (last?.stream && shown.length === n) {
      o.dirty = false;
      this.check(execution.replaceOutputItems(toItems(last), shown[n - 1]), cellId);
    } else if (last?.stream && shown.length === n - 1) {
      o.dirty = false;
      const output = toCellOutput(last);
      shown.push(output);
      this.check(execution.appendOutput(output), cellId);
    } else {
      this.writeAll(cellId, execution);
    }
  }

  private writeAll(cellId: string, execution: vscode.NotebookCellExecution) {
    const o = this.outputsOf(cellId);
    clearTimeout(o.timer);
    o.timer = undefined;
    o.shown = o.outputs.map(toCellOutput);
    o.dirty = false;
    this.check(execution.replaceOutput(o.shown), cellId);
  }

  private check(write: Thenable<void>, cellId: string) {
    write.then(undefined, (e) => this.host.log(`Couldn't show the output of cell '${cellId}' in ${this.name}: ${(e as Error).message}`));
  }

  /** The VS Code cell for a server cell key, or undefined. */
  cell(cellId: string): vscode.NotebookCell | undefined {
    return this.doc.getCells().find((c) => keyOf(c) === cellId);
  }

  private parts(): NotebookPartsData {
    return cellsToParts(this.doc.getCells().map(toPlainCell), readNotebookMetadata(this.doc.metadata));
  }

  private get name(): string {
    return this.path.slice(Math.max(this.path.lastIndexOf("/"), this.path.lastIndexOf("\\")) + 1);
  }
}

/** The id the server uses for a cell: "setup", "types", or the cell's Reverb id. */
export function keyOf(cell: vscode.NotebookCell): string | undefined {
  const m = cell.metadata.reverb as PlainCell["metadata"]["reverb"];
  if (m?.kind === "setup" || m?.kind === "types") return m.kind;
  return m?.id;
}

export function summarize(e: vscode.NotebookDocumentChangeEvent): ChangeSummary {
  return {
    content: e.contentChanges.map((c) => ({ start: c.range.start, end: c.range.end, removed: c.removedCells.length, added: c.addedCells.length })),
    cellMetadata: e.cellChanges.some((c) => c.metadata),
    text: e.cellChanges.some((c) => c.document),
    notebookMetadata: e.metadata !== undefined,
  };
}

function toPlainCell(cell: vscode.NotebookCell): PlainCell {
  return {
    kind: cell.kind === vscode.NotebookCellKind.Markup ? "markup" : "code",
    source: cell.document.getText(),
    metadata: { reverb: cell.metadata.reverb },
  };
}

function toItems(output: Output): vscode.NotebookCellOutputItem[] {
  return toOutputItems(output).map((i) =>
    typeof i.data === "string" ? vscode.NotebookCellOutputItem.text(i.data, i.mime) : new vscode.NotebookCellOutputItem(i.data, i.mime),
  );
}

function toCellOutput(output: Output): vscode.NotebookCellOutput {
  const e = errorItem(output);
  if (!e) return new vscode.NotebookCellOutput(toItems(output));
  const error = new Error(e.message);
  error.name = e.name;
  error.stack = e.stack;
  return new vscode.NotebookCellOutput([vscode.NotebookCellOutputItem.error(error)]);
}
