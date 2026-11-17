// Converts between the server's notebook parts and the cells and metadata VS Code keeps.
// No vscode import, so it can be unit tested in plain Node.
import type { CellAttributeData, CellKindName, NotebookPartsData, PartCell, PreambleLine, RawCellData } from "./protocol";

/** Stored under `reverb` in each cell's metadata. */
export interface ReverbCellMetadata {
  id?: string;
  kind: CellKindName;
  attrs: CellAttributeData[];
  raw?: RawCellData;
}

/** Stored under `reverb` in the notebook's metadata. */
export interface ReverbNotebookMetadata {
  preamble: PreambleLine[];
  newline: string;
  bom: boolean;
}

/** A cell as the serializer sees it, without the vscode types. */
export interface PlainCell {
  kind: "code" | "markup";
  source: string;
  metadata: { reverb?: ReverbCellMetadata };
}

export function partsToCells(parts: NotebookPartsData): { cells: PlainCell[]; metadata: ReverbNotebookMetadata } {
  return {
    cells: parts.cells.map(toCell),
    metadata: { preamble: parts.preamble ?? [], newline: parts.newline, bom: parts.bom },
  };
}

export function cellsToParts(cells: PlainCell[], metadata: ReverbNotebookMetadata): NotebookPartsData {
  return {
    preamble: metadata.preamble,
    newline: metadata.newline,
    bom: metadata.bom,
    cells: cells.map(toPart),
  };
}

/** The notebook's Reverb metadata, or what a new file gets when it has none. */
export function readNotebookMetadata(notebookMetadata: unknown): ReverbNotebookMetadata {
  const m = (notebookMetadata as { reverb?: Partial<ReverbNotebookMetadata> } | undefined)?.reverb;
  return {
    preamble: Array.isArray(m?.preamble) ? m.preamble : [],
    newline: typeof m?.newline === "string" ? m.newline : "\n",
    bom: m?.bom === true,
  };
}

function toCell(c: PartCell): PlainCell {
  const reverb: ReverbCellMetadata = { kind: c.kind, attrs: c.attributes ?? [] };
  if (c.id != null) reverb.id = c.id;
  if (c.raw != null) reverb.raw = c.raw;
  return { kind: c.kind === "markdown" ? "markup" : "code", source: c.source, metadata: { reverb } };
}

function toPart(cell: PlainCell): PartCell {
  // A cell with no Reverb metadata was pasted or added by something else. The server gives it an id.
  const m = cell.metadata?.reverb;
  return {
    kind: kindOf(cell),
    id: m?.id ?? null,
    source: cell.source,
    attributes: m?.attrs ?? [],
    raw: m?.raw ?? null,
  };
}

// VS Code's own cell kind decides between markdown and code, since the user can switch it there.
function kindOf(cell: PlainCell): CellKindName {
  if (cell.kind === "markup") return "markdown";
  const kind = cell.metadata?.reverb?.kind;
  return kind === "setup" || kind === "types" ? kind : "code";
}
