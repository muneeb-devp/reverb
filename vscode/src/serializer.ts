// Reading and writing go through reverb (format.parse and format.serialize), so the file is
// written by the same code as `reverb edit` and unchanged cells keep their bytes. Neither runs
// notebook code, so this works in Restricted Mode too.
import * as vscode from "vscode";
import { cellsToParts, partsToCells, readNotebookMetadata, type PlainCell } from "./metadata";
import type { FormatParsed, FormatSerialized } from "./protocol";
import { saveFailureMessage, type ReverbProcess } from "./reverbProcess";

const CODE_LANGUAGE = "reverb-csharp";

export class ReverbSerializer implements vscode.NotebookSerializer {
  constructor(private proc: () => Promise<ReverbProcess>) {}

  async deserializeNotebook(content: Uint8Array): Promise<vscode.NotebookData> {
    const text = decodeNotebook(content);
    let reply: FormatParsed;
    try {
      reply = await (await this.proc()).request<FormatParsed>({ type: "format.parse", text });
    } catch (e) {
      throw new Error(`Reverb couldn't read this notebook: ${(e as Error).message}`);
    }
    const { cells, metadata } = partsToCells(reply.parts);
    const data = new vscode.NotebookData(cells.map(toCellData));
    data.metadata = { reverb: metadata };
    return data;
  }

  async serializeNotebook(data: vscode.NotebookData): Promise<Uint8Array> {
    const parts = cellsToParts(data.cells.map(toPlainCell), readNotebookMetadata(data.metadata));
    let reply: FormatSerialized;
    try {
      reply = await (await this.proc()).request<FormatSerialized>({ type: "format.serialize", parts });
    } catch (e) {
      throw new Error(saveFailureMessage(e as Error));
    }
    return new TextEncoder().encode(reply.text);
  }
}

/**
 * A notebook file's text. A leading BOM stays in the text: the server records it in the parts and
 * writes it back. Bytes that aren't UTF-8 would be replaced and the next save would change them, so
 * they're refused.
 */
export function decodeNotebook(content: Uint8Array): string {
  try {
    return new TextDecoder("utf-8", { ignoreBOM: true, fatal: true }).decode(content);
  } catch {
    throw new Error("This file isn't valid UTF-8, so Reverb can't open it without changing it. Save it as UTF-8 and open it again.");
  }
}

function toCellData(cell: PlainCell): vscode.NotebookCellData {
  const markup = cell.kind === "markup";
  const data = new vscode.NotebookCellData(
    markup ? vscode.NotebookCellKind.Markup : vscode.NotebookCellKind.Code,
    cell.source,
    markup ? "markdown" : CODE_LANGUAGE,
  );
  data.metadata = cell.metadata;
  return data;
}

function toPlainCell(cell: vscode.NotebookCellData): PlainCell {
  return {
    kind: cell.kind === vscode.NotebookCellKind.Markup ? "markup" : "code",
    source: cell.value,
    metadata: { reverb: cell.metadata?.reverb },
  };
}
