// How the kernel's language answers map onto VS Code's. No vscode import, so it can be unit tested.
// The kinds match frontend/src/language.ts.
import type { Diagnostic } from "./protocol";

const kinds: Record<string, string> = {
  variable: "Variable",
  method: "Method",
  property: "Property",
  field: "Field",
  event: "Event",
  class: "Class",
  struct: "Struct",
  interface: "Interface",
  enum: "Enum",
  "enum-member": "EnumMember",
  namespace: "Module",
  keyword: "Keyword",
  type: "TypeParameter",
  snippet: "Snippet",
};

/** The name of a vscode.CompletionItemKind member for a kernel completion kind. */
export function completionKindName(kind: string): string {
  return kinds[kind] ?? "Text";
}

/** The name of a vscode.DiagnosticSeverity member, or undefined for diagnostics that aren't shown. */
export function severityName(severity: Diagnostic["severity"]): "Error" | "Warning" | "Information" | undefined {
  switch (severity) {
    case "error":
      return "Error";
    case "warning":
      return "Warning";
    case "info":
      return "Information";
    default:
      return undefined;
  }
}

/** [startLine, startColumn, endLine, endColumn], 0-based as the server sends them. */
export function diagnosticRange(d: Pick<Diagnostic, "startLine" | "startColumn" | "endLine" | "endColumn">): [number, number, number, number] {
  return [d.startLine, d.startColumn, d.endLine, d.endColumn];
}

/** Where a parameter is in a signature label, searched for after the '('. Its own text if it isn't there. */
export function parameterLabel(label: string, parameter: string): [number, number] | string {
  const at = label.indexOf(parameter, Math.max(label.indexOf("("), 0));
  return at >= 0 ? [at, at + parameter.length] : parameter;
}
