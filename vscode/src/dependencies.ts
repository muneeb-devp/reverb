// Which cells use which, from the server's edges. Pure, so the wording is testable.
import type { Edge } from "./protocol";

export interface Dependencies {
  /** Cells this one reads from, with the names it reads. */
  uses: { cell: string; names: string[] }[];
  /** Cells that read from this one. */
  usedBy: string[];
}

/** An edge `{ from, to, via }` means cell `from` uses the names `via` from cell `to`. The types cell isn't a cell to show. */
export function dependenciesOf(cellId: string, edges: readonly Edge[]): Dependencies {
  const deps: Dependencies = { uses: [], usedBy: [] };
  for (const e of edges) {
    if (e.to === "types") continue;
    if (e.from === cellId) deps.uses.push({ cell: e.to, names: e.via });
    if (e.to === cellId) deps.usedBy.push(e.from);
  }
  return deps;
}

function cells(n: number): string {
  return n === 1 ? "1 cell" : `${n} cells`;
}

/** The status bar items for a cell's dependencies. `label` names a cell by its id. */
export function dependencyItems(deps: Dependencies, label: (id: string) => string): { text: string; tooltip: string }[] {
  const items: { text: string; tooltip: string }[] = [];
  if (deps.uses.length > 0) {
    items.push({
      text: `uses ${cells(deps.uses.length)}`,
      tooltip: `Uses ${deps.uses.map((u) => `${u.names.join(", ")} from ${label(u.cell)}`).join("; ")}`,
    });
  }
  if (deps.usedBy.length > 0) {
    items.push({
      text: `used by ${cells(deps.usedBy.length)}`,
      tooltip: `Used by: ${deps.usedBy.map(label).join(", ")}`,
    });
  }
  return items;
}
