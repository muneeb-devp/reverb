// Turns the server's cell statuses into VS Code execution steps. Pure, so it runs in plain Node.
// The engine decides what runs, including cells VS Code didn't ask for, so these statuses are the
// only thing that starts or ends an execution.
import type { CellStatus } from "./protocol";

export type ExecAction =
  | { kind: "create"; cellId: string } // queued: create, don't start
  | { kind: "start"; cellId: string; order: number; startTime: number }
  | { kind: "end"; cellId: string; success: boolean; endTime?: number }
  | { kind: "order"; cellId: string; order: number }; // ran again within the same execution

export type ExecState = "created" | "started";

export class ExecutionTracker {
  private open = new Map<string, ExecState>();

  /**
   * `now` is when the status arrived. Start and end times are these receipt times; the elapsed
   * time the server reports with a finished cell isn't used.
   */
  onStatus(cellId: string, status: CellStatus, runs: number, now: number): ExecAction[] {
    const state = this.open.get(cellId);
    switch (status) {
      case "queued":
        if (state) return [];
        this.open.set(cellId, "created");
        return [{ kind: "create", cellId }];
      case "running": {
        // VS Code can't start an execution twice, so a second "running" keeps the first one
        // and only takes the new run count.
        if (state === "started") return [{ kind: "order", cellId, order: runs }];
        const actions: ExecAction[] = state ? [] : [{ kind: "create", cellId }];
        this.open.set(cellId, "started");
        actions.push({ kind: "start", cellId, order: runs, startTime: now });
        return actions;
      }
      case "idle":
      case "error":
      case "upstream-error":
        if (!state) return [];
        this.open.delete(cellId);
        return [{ kind: "end", cellId, success: status === "idle", endTime: now }];
      case "stale":
      case "disabled":
        if (!state) return [];
        this.open.delete(cellId);
        return [{ kind: "end", cellId, success: false }];
    }
  }

  /** Ends everything that's open as failed: the process or the kernel went away. */
  endAll(): ExecAction[] {
    const actions: ExecAction[] = [...this.open.keys()].map((cellId) => ({ kind: "end", cellId, success: false }));
    this.open.clear();
    return actions;
  }

  /** The cell was deleted, so no status will end its execution. */
  forget(cellId: string): ExecAction[] {
    if (!this.open.delete(cellId)) return [];
    return [{ kind: "end", cellId, success: false }];
  }

  state(cellId: string): ExecState | undefined {
    return this.open.get(cellId);
  }
}
