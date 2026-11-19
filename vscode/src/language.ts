// Completions, hover, signature help and diagnostics for cells. The answers come from Reverb's own
// language services (the ones the browser editor uses), through the notebook's session.
import * as vscode from "vscode";
import { completionKindName, diagnosticRange, parameterLabel, severityName } from "./languageMapping";
import type { Diagnostic } from "./protocol";
import { keyOf, type LanguageAnswer, type LanguageRequest, type NotebookSession } from "./session";

const SELECTOR: vscode.DocumentSelector = { notebookType: "reverb", language: "reverb-csharp" };

/** The session for the notebook a cell document belongs to, when there is one and the folder is trusted. */
export type SessionFor = (notebookUri: vscode.Uri) => NotebookSession | undefined;

export interface Target {
  session: NotebookSession;
  cellId: string;
}

/** The session and cell key of a cell document, when the folder is trusted and the notebook is open in Reverb. */
export function targetOf(document: vscode.TextDocument, sessionFor: SessionFor): Target | undefined {
  if (!vscode.workspace.isTrusted) return undefined;
  for (const nb of vscode.workspace.notebookDocuments) {
    const cell = nb.getCells().find((c) => c.document === document);
    if (!cell) continue;
    const cellId = keyOf(cell);
    const session = sessionFor(nb.uri);
    return session && cellId ? { session, cellId } : undefined;
  }
  return undefined;
}

async function ask(
  sessionFor: SessionFor,
  type: LanguageRequest,
  document: vscode.TextDocument,
  position: vscode.Position,
  token: vscode.CancellationToken,
): Promise<LanguageAnswer | undefined> {
  const target = targetOf(document, sessionFor);
  if (!target) return undefined;
  return target.session.requestLanguage(type, target.cellId, document.getText(), document.offsetAt(position), token);
}

/** Registers the providers for Reverb cells. */
export function registerLanguageServices(sessionFor: SessionFor): vscode.Disposable[] {
  return [
    vscode.languages.registerCompletionItemProvider(
      SELECTOR,
      {
        async provideCompletionItems(document, position, token) {
          const answer = await ask(sessionFor, "lang.completions", document, position, token);
          if (answer?.type !== "lang.completionsResult" || !answer.completions) return undefined;
          const range = new vscode.Range(document.positionAt(answer.from ?? document.offsetAt(position)), position);
          const kinds = vscode.CompletionItemKind as unknown as Record<string, vscode.CompletionItemKind>;
          return answer.completions.map((c) => {
            const item = new vscode.CompletionItem(c.label, kinds[completionKindName(c.kind)]);
            if (c.insert) item.insertText = c.insert;
            if (c.detail) item.detail = c.detail;
            item.sortText = c.sortText;
            item.range = range;
            return item;
          });
        },
      },
      ".",
    ),
    vscode.languages.registerHoverProvider(SELECTOR, {
      async provideHover(document, position, token) {
        const answer = await ask(sessionFor, "lang.hover", document, position, token);
        if (answer?.type !== "lang.hoverResult" || !answer.text) return undefined;
        const range =
          answer.from !== undefined && answer.to !== undefined
            ? new vscode.Range(document.positionAt(answer.from), document.positionAt(answer.to))
            : undefined;
        return new vscode.Hover(new vscode.MarkdownString(answer.text), range);
      },
    }),
    vscode.languages.registerSignatureHelpProvider(
      SELECTOR,
      {
        async provideSignatureHelp(document, position, token) {
          const answer = await ask(sessionFor, "lang.signatureHelp", document, position, token);
          if (answer?.type !== "lang.signatureHelpResult" || !answer.signatures?.length) return undefined;
          const help = new vscode.SignatureHelp();
          help.signatures = answer.signatures.map((s) => {
            const info = new vscode.SignatureInformation(s.label);
            info.parameters = s.parameters.map((p) => new vscode.ParameterInformation(parameterLabel(s.label, p)));
            return info;
          });
          help.activeSignature = Math.min(Math.max(answer.activeSignature ?? 0, 0), help.signatures.length - 1);
          help.activeParameter = answer.activeParameter ?? 0;
          return help;
        },
      },
      "(",
      ",",
    ),
  ];
}

interface NotebookDiagnostics {
  cells: Map<string, Diagnostic[]>;
  notebook: Diagnostic[];
  /** The cell documents that have diagnostics in the collection now. */
  shown: Set<string>;
  /** Notebook-level errors already announced. */
  announced: Set<string>;
}

/** The "reverb" diagnostics of every open notebook, as the server last reported them. */
export class ReverbDiagnostics implements vscode.Disposable {
  readonly collection = vscode.languages.createDiagnosticCollection("reverb");
  private notebooks = new Map<string, NotebookDiagnostics>();

  /** Sets the diagnostics of a cell, or of the notebook when `cellId` is undefined. */
  set(session: NotebookSession, cellId: string | undefined, diagnostics: Diagnostic[]): void {
    if (!vscode.workspace.isTrusted) return;
    const state = this.state(session);
    if (cellId === undefined) {
      state.notebook = diagnostics;
      this.announce(session, state);
    } else if (diagnostics.length > 0) state.cells.set(cellId, diagnostics);
    else state.cells.delete(cellId);
    this.render(session, state);
  }

  /** The server's diagnostic that `shown` was made from, on the cell with key `cellId`. */
  find(session: NotebookSession, cellId: string, shown: vscode.Diagnostic): Diagnostic | undefined {
    const list = this.notebooks.get(session.doc.uri.toString())?.cells.get(cellId) ?? [];
    return list.find((d) => d.id === shown.code && d.message === shown.message && d.startLine === shown.range.start.line && d.startColumn === shown.range.start.character);
  }

  /** Drops a cell's diagnostics, or all of the notebook's. */
  clear(session: NotebookSession, cellId?: string): void {
    const k = session.doc.uri.toString();
    const state = this.notebooks.get(k);
    if (!state) return;
    if (cellId === undefined) {
      this.notebooks.delete(k);
      for (const uri of state.shown) this.collection.delete(vscode.Uri.parse(uri));
      return;
    }
    state.cells.delete(cellId);
    this.render(session, state);
  }

  dispose(): void {
    this.collection.dispose();
    this.notebooks.clear();
  }

  private state(session: NotebookSession): NotebookDiagnostics {
    const k = session.doc.uri.toString();
    let state = this.notebooks.get(k);
    if (!state) this.notebooks.set(k, (state = { cells: new Map(), notebook: [], shown: new Set(), announced: new Set() }));
    return state;
  }

  // Everything is written again each time, so a cell that moved or was deleted can't keep a stale entry.
  private render(session: NotebookSession, state: NotebookDiagnostics) {
    const byCell = new Map<string, vscode.Diagnostic[]>();
    const add = (uri: vscode.Uri, list: Diagnostic[]) => {
      const key = uri.toString();
      const all = byCell.get(key) ?? [];
      for (const d of list) {
        const severity = severityName(d.severity);
        if (severity === undefined) continue;
        const [sl, sc, el, ec] = diagnosticRange(d);
        const out = new vscode.Diagnostic(new vscode.Range(sl, sc, el, ec), d.message, vscode.DiagnosticSeverity[severity]);
        out.source = "reverb";
        out.code = d.id;
        all.push(out);
      }
      byCell.set(key, all);
    };
    for (const [cellId, list] of state.cells) {
      const cell = session.cell(cellId);
      if (cell) add(cell.document.uri, list);
    }
    const first = session.doc.getCells()[0];
    if (first && state.notebook.length > 0) add(first.document.uri, state.notebook);
    for (const uri of state.shown) if (!byCell.has(uri)) this.collection.delete(vscode.Uri.parse(uri));
    for (const [uri, list] of byCell) this.collection.set(vscode.Uri.parse(uri), list);
    state.shown = new Set(byCell.keys());
  }

  private announce(session: NotebookSession, state: NotebookDiagnostics) {
    const name = session.doc.uri.path.slice(session.doc.uri.path.lastIndexOf("/") + 1);
    for (const d of state.notebook) {
      if (d.severity !== "error" || state.announced.has(d.message)) continue;
      state.announced.add(d.message);
      void vscode.window.showInformationMessage(`${name}: ${d.message}`);
    }
  }
}
