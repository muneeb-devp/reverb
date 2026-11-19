// The messages the renderer sends. No vscode import, so the unit tests can load it.
import type { WidgetKind } from "../../frontend/src/protocol";

const KINDS: readonly string[] = ["slider", "dropdown", "text", "checkbox", "number", "button"];

export interface WidgetChange {
  widgetId: string;
  value: unknown;
  kind: WidgetKind;
}

/** The change in a message from the renderer, or undefined when it isn't a widget change. */
export function parseWidgetMessage(m: unknown): WidgetChange | undefined {
  if (typeof m !== "object" || m === null) return undefined;
  const { type, widgetId, value, kind } = m as Record<string, unknown>;
  if (type !== "widget.setValue" || typeof widgetId !== "string" || widgetId === "") return undefined;
  if (typeof kind !== "string" || !KINDS.includes(kind)) return undefined;
  return { widgetId, value, kind: kind as WidgetKind };
}
