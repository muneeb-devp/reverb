// Messages between the browser and the Reverb server. Mirrors Reverb.Server/BrowserProtocol.cs.

export type CellKind = "code" | "markdown" | "setup" | "types";

export type CellStatus = "idle" | "queued" | "running" | "stale" | "error" | "upstream-error" | "disabled";

/** "building" means getting the notebook's packages and building a kernel with them. */
export type KernelState = "building" | "starting" | "ready" | "busy" | "dead";

export interface Package {
  name: string;
  version?: string;
}

export interface CellAttrs {
  hide_code?: boolean;
  disabled?: boolean;
  name?: string | null;
}

/** One output. Stream text has `stream` and `text`; anything else has MIME `data`. */
export interface Output {
  data?: Record<string, string>;
  title?: string;
  stream?: "stdout" | "stderr";
  text?: string;
}

/** Ranges are zero-based and relative to the cell. */
export interface Diagnostic {
  id: string;
  severity: "error" | "warning" | "info" | "hidden";
  message: string;
  cell?: string;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
  /** What Reverb's fix does, when it has one. */
  fix?: string;
}

export interface Completion {
  label: string;
  /** Text to insert when it isn't the label. */
  insert?: string;
  kind: string;
  detail?: string;
  sortText: string;
}

export interface Signature {
  label: string;
  parameters: string[];
}

export type LangRequest = "lang.completions" | "lang.hover" | "lang.signatureHelp";

/** Cell `from` uses the names in `via` from cell `to`. */
export interface Edge {
  from: string;
  to: string;
  via: string[];
}

export interface CellView {
  id: string;
  kind: CellKind;
  source: string;
  attrs: CellAttrs;
  status: CellStatus;
  elapsedMs?: number;
  message?: string;
  outputs: Output[];
  diagnostics: Diagnostic[];
  edited: boolean;
  /** How many times the cell has started running since the editor opened. */
  runs: number;
}

export type ServerMessage =
  | {
      type: "notebook.snapshot";
      mode: "edit" | "app";
      notebookVersion: number;
      ack: number;
      path: string;
      name: string;
      lazy: boolean;
      cells: CellView[];
      packages: Package[];
      kernel: { state: KernelState; message?: string };
      notebookDiagnostics: Diagnostic[];
      edges: Edge[];
    }
  | { type: "cell.status"; cellId: string; status: CellStatus; elapsedMs?: number; message?: string; runs?: number }
  | { type: "cell.outputs"; cellId: string; outputs: Output[] }
  | { type: "cell.stream"; cellId: string; stream: "stdout" | "stderr"; text: string }
  | { type: "cell.diagnostics"; cellId?: string; notebookVersion: number; diagnostics: Diagnostic[] }
  | { type: "graph.update"; notebookVersion: number; edges: Edge[] }
  | { type: "kernel.status"; state: KernelState; message?: string }
  | { type: "file.saved"; notebookVersion: number; ack: number }
  | { type: "file.externalChange"; notebookVersion: number; message: string }
  | { type: "error"; message: string }
  | { type: "lang.completionsResult"; requestId: number; from?: number; to?: number; completions?: Completion[] }
  | { type: "lang.hoverResult"; requestId: number; from?: number; to?: number; text?: string }
  | {
      type: "lang.signatureHelpResult";
      requestId: number;
      signatures?: Signature[];
      activeSignature?: number;
      activeParameter?: number;
    };

export type ClientMessage =
  | { type: "cell.update"; cellId: string; source: string }
  | { type: "cell.run"; cellId: string }
  | { type: "notebook.runAll" }
  | { type: "notebook.setLazy"; lazy: boolean }
  | { type: "notebook.save" }
  | { type: "cell.create"; cellId: string; afterCellId: string | null; kind: "code" | "markdown"; source?: string }
  | { type: "cell.delete"; cellId: string }
  | { type: "cell.move"; cellId: string; afterCellId: string | null }
  | { type: "cell.setAttrs"; cellId: string; attrs: CellAttrs }
  | { type: "widget.setValue"; widgetId: string; value: unknown; kind: WidgetKind }
  | { type: "kernel.interrupt" }
  | { type: "kernel.restart" }
  | { type: "packages.set"; packages: Package[] }
  | { type: LangRequest; requestId: number; cellId: string; source: string; offset: number }
  | { type: "lang.codeFix"; cellId: string; diagnosticId: string; line: number; column: number };

export const MIME = {
  plain: "text/plain",
  markdown: "text/markdown",
  html: "text/html",
  svg: "image/svg+xml",
  png: "image/png",
  jpeg: "image/jpeg",
  table: "application/vnd.reverb.table+json",
  object: "application/vnd.reverb.object+json",
  widget: "application/vnd.reverb.widget+json",
  error: "application/vnd.reverb.error+json",
  vegaLite: "application/vnd.vegalite.v5+json",
} as const;

export type WidgetKind = "slider" | "dropdown" | "text" | "textarea" | "checkbox" | "number" | "button";

/** A widget as the kernel describes it (application/vnd.reverb.widget+json). */
export interface WidgetSpec {
  kind: WidgetKind;
  label: string;
  /** Missing when the widget isn't live (it can't be changed). */
  id?: string;
  value: number | string | boolean | null;
  min?: number;
  max?: number;
  step?: number;
  /** Dropdowns: option texts and the selected index. */
  options?: string[];
  index?: number;
  /** Text areas: how many lines show before it scrolls. */
  rows?: number;
}
