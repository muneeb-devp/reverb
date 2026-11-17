// Reading VS Code's notebook changes and the server's corrected cell lists. No vscode import, so
// it can be unit tested in plain Node.
import type { CellKindName, PartCell } from "./protocol";

export const TYPING_SAVE_DELAY_MS = 1000;

/** A cell range VS Code replaced: `removed` cells from start to end, `added` cells in their place. */
export interface ContentChange {
  start: number;
  end: number;
  removed: number;
  added: number;
}

/** The parts of a NotebookDocumentChangeEvent that matter here. */
export interface ChangeSummary {
  content: ContentChange[];
  /** A cell's metadata changed (its Reverb id, kind or attributes). */
  cellMetadata: boolean;
  /** A cell's text changed. */
  text: boolean;
  /** The notebook's metadata changed: the preamble, which holds packages and lazy mode. */
  notebookMetadata: boolean;
}

/**
 * VS Code may have read the file again: after it changed on disk, or for File: Revert. VS Code then
 * replaces only the cells that differ, but it always sets the notebook's metadata (even to the same
 * value). Users don't edit notebook metadata; the session does (`ownEdit`). Whether it really was
 * a reload is settled by the notebook being clean afterwards, which the event can't tell yet.
 */
export function isReload(change: ChangeSummary, ownEdit: boolean): boolean {
  return change.notebookMetadata && !ownEdit;
}

/**
 * Whether a server error about a notebook means reverb has no host for it: the host failed to
 * open, or the notebook was closed there. The server only says so in the error's text.
 */
export function notebookLost(message: string): boolean {
  return message.includes(" isn't open in Reverb") || / couldn't be opened: /.test(message);
}

/** How long to wait before saving after a change, or undefined when the file isn't affected. */
export function saveDelay(change: ChangeSummary): number | undefined {
  if (change.content.length > 0 || change.cellMetadata || change.notebookMetadata) return 0;
  if (change.text) return TYPING_SAVE_DELAY_MS;
  return undefined;
}

/**
 * For each cell of the server's corrected list, its index in the list that was sent. The server
 * turns a second setup or types cell into code, then puts setup first and types last and keeps
 * the rest in order. Undefined when the lists can't be the same cells, because the cell count
 * changed since the list was sent.
 */
export function correctionOrder(sent: readonly CellKindName[], corrected: readonly PartCell[]): number[] | undefined {
  if (sent.length !== corrected.length) return undefined;
  let setup = false;
  let types = false;
  const kinds = sent.map((k): CellKindName => {
    if (k === "setup") {
      if (setup) return "code";
      setup = true;
    }
    if (k === "types") {
      if (types) return "code";
      types = true;
    }
    return k;
  });
  const all = kinds.map((_, i) => i);
  const order = [
    ...all.filter((i) => kinds[i] === "setup"),
    ...all.filter((i) => kinds[i] !== "setup" && kinds[i] !== "types"),
    ...all.filter((i) => kinds[i] === "types"),
  ];
  return order.every((i, j) => corrected[j].kind === kinds[i]) ? order : undefined;
}
