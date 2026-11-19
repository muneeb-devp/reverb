// Turns the cell list a Reverb fix produced into edits of the notebook VS Code has. No vscode
// import, so it can be unit tested.
import type { CellKindName, PartCell } from "./protocol";

export interface CurrentCell {
  kind: CellKindName;
  id?: string;
  source: string;
}

export interface FixEdits {
  /** New text for the cell at `index`. */
  replace: { index: number; source: string }[];
  /** A cell to insert before the cell at `index` of the notebook as it is now. In list order when indexes are equal. */
  insert: { index: number; cell: PartCell }[];
}

/**
 * Setup and types cells match by kind, the others by id. A fix only adds cells and changes text,
 * so a cell missing from `fixed` is left as it is.
 */
export function planFixEdits(current: readonly CurrentCell[], fixed: readonly PartCell[]): FixEdits {
  const at = new Map<string, number>();
  current.forEach((c, i) => {
    const k = matchKey(c.kind, c.id);
    if (k !== undefined && !at.has(k)) at.set(k, i);
  });
  const edits: FixEdits = { replace: [], insert: [] };
  let previous = -1;
  for (const cell of fixed) {
    const k = matchKey(cell.kind, cell.id);
    const index = k === undefined ? undefined : at.get(k);
    if (index !== undefined) {
      if (current[index].source !== cell.source) edits.replace.push({ index, source: cell.source });
      previous = index;
      continue;
    }
    const where = cell.kind === "setup" ? 0 : cell.kind === "types" ? current.length : previous + 1;
    edits.insert.push({ index: where, cell });
  }
  return edits;
}

function matchKey(kind: CellKindName, id: string | null | undefined): string | undefined {
  if (kind === "setup" || kind === "types") return kind;
  return id ? `id:${id}` : undefined;
}
