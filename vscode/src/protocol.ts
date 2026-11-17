// Messages between the extension and `reverb serve --stdio`, one JSON object per line.
// The notebook messages are the browser protocol, so those types are shared with the frontend.
export * from "../../frontend/src/protocol";

import type { ServerMessage as BrowserServerMessage } from "../../frontend/src/protocol";

export const PROTOCOL_VERSION = 1;

// The parts types mirror src/Reverb.Format/NotebookParts.cs. The server leaves out null fields,
// so anything nullable can arrive as undefined too.

export type CellKindName = "setup" | "code" | "markdown" | "types";

export interface PreambleLine {
  text: string;
  terminator: string;
}

/**
 * A browser server message. The stdio server's snapshot also carries the preamble, the lines
 * before the first cell, which the browser doesn't use. `notebook.cells` is the corrected cell
 * list, sent back to the editor that synced a list that needed fixing.
 */
export type ServerMessage =
  | Exclude<BrowserServerMessage, { type: "notebook.snapshot" }>
  | (Extract<BrowserServerMessage, { type: "notebook.snapshot" }> & { preamble?: PreambleLine[] })
  | { type: "notebook.cells"; partCells: PartCell[] };

export interface CellAttributeData {
  key: string;
  value?: string | null;
}

/** The cell's text as it was read, so an unchanged cell is written back byte for byte. */
export interface RawCellData {
  markerLine: string;
  leadingPad: string;
  content: string;
  trailingPad: string;
  wasLast: boolean;
  kind: CellKindName;
  id?: string | null;
  attributes: CellAttributeData[];
  source: string;
  markerLineNumber: number;
}

export interface PartCell {
  kind: CellKindName;
  id?: string | null;
  source: string;
  attributes: CellAttributeData[];
  raw?: RawCellData | null;
}

export interface NotebookPartsData {
  preamble: PreambleLine[];
  newline: string;
  bom: boolean;
  cells: PartCell[];
}

export interface HelloResult {
  type: "hello.result";
  requestId: number;
  protocolVersion: number;
  reverbVersion: string;
}

export interface FormatParsed {
  type: "format.parsed";
  requestId: number;
  parts: NotebookPartsData;
}

export interface FormatSerialized {
  type: "format.serialized";
  requestId: number;
  text: string;
}

export interface FormatTemplated {
  type: "format.templated";
  requestId: number;
  text: string;
}

/** Anything the server can send. Host messages carry `notebook`, the path the client opened. */
export type StdioMessage = (ServerMessage | HelloResult | FormatParsed | FormatSerialized | FormatTemplated) & {
  notebook?: string;
  requestId?: number;
};
