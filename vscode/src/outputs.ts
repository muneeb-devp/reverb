// Turns the server's outputs into what VS Code's built-in renderers draw. Pure, so it runs in
// plain Node; the session wraps the results in NotebookCellOutputItems.
import { MIME, type Output } from "./protocol";

export const STDOUT_MIME = "application/vnd.code.notebook.stdout";
export const STDERR_MIME = "application/vnd.code.notebook.stderr";

export interface OutputItemDesc {
  mime: string;
  data: string | Uint8Array;
}

// Richest first. Reverb's own table, object, widget and chart mimes have no renderer in VS Code
// yet, so they're left out and the HTML or text that comes with them is shown.
const SHOWN = [MIME.html, MIME.markdown, MIME.png, MIME.jpeg, MIME.svg, MIME.plain];
const BASE64 = new Set<string>([MIME.png, MIME.jpeg]);

/** The items to show for one output. An error output gives none: use errorItem for it. */
export function toOutputItems(output: Output): OutputItemDesc[] {
  if (output.stream) return [{ mime: output.stream === "stderr" ? STDERR_MIME : STDOUT_MIME, data: output.text ?? "" }];
  if (errorItem(output)) return [];
  const data = output.data ?? {};
  return SHOWN.filter((mime) => data[mime] !== undefined).map((mime) => ({
    mime,
    data: BASE64.has(mime) ? new Uint8Array(Buffer.from(data[mime], "base64")) : data[mime],
  }));
}

/**
 * An exception from a cell, shaped for NotebookCellOutputItem.error. The renderer shows the
 * stack, so it starts with the type, message and the cell and line, like a JavaScript stack.
 */
export function errorItem(output: Output): { name: string; message: string; stack: string } | undefined {
  const json = output.data?.[MIME.error];
  if (json === undefined) return undefined;
  let e: { type?: unknown; message?: unknown; location?: unknown; stackTrace?: unknown };
  try {
    e = JSON.parse(json);
    if (e === null || typeof e !== "object") return undefined;
  } catch {
    return undefined;
  }
  const fullName = typeof e.type === "string" ? e.type : "Error";
  const name = fullName.slice(fullName.lastIndexOf(".") + 1);
  const location = typeof e.location === "string" ? `\n  at ${e.location}` : "";
  const message = `${typeof e.message === "string" ? e.message : ""}${location}`;
  const trace = typeof e.stackTrace === "string" && e.stackTrace ? `\n${e.stackTrace}` : "";
  return { name, message, stack: `${name}: ${message}${trace}` };
}

/** Adds stream text the way the server does: to the last output if it's the same stream. */
export function appendStream(outputs: Output[], stream: "stdout" | "stderr", text: string): Output[] {
  const last = outputs[outputs.length - 1];
  if (last?.stream === stream) return [...outputs.slice(0, -1), { stream, text: (last.text ?? "") + text }];
  return [...outputs, { stream, text }];
}
