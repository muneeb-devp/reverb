// What the renderer sends to the extension. No DOM or React here, so the unit tests can load it.
import type { ClientMessage, WidgetKind } from "../../../frontend/src/protocol";

export type WidgetSetValue = Extract<ClientMessage, { type: "widget.setValue" }>;

export function widgetMessage(widgetId: string, value: unknown, kind: WidgetKind): WidgetSetValue {
  return { type: "widget.setValue", widgetId, value, kind };
}

/** A WidgetSender for the frontend's widgets, or null when there's no messaging, which disables them. */
export function widgetSender(postMessage: ((message: unknown) => void) | undefined): ((id: string, value: unknown, kind: WidgetKind) => void) | null {
  if (!postMessage) return null;
  return (id, value, kind) => postMessage(widgetMessage(id, value, kind));
}
