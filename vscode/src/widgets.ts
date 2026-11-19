// Widget changes from the renderer go to the kernel of the notebook they came from.
import * as vscode from "vscode";
import type { ReverbController } from "./controller";
import { parseWidgetMessage } from "./widgetMessage";

export { parseWidgetMessage };

let loggedNoSession = false;

/** Lets the next widget change without a session be logged again, as trust may have changed what's wrong. */
export function resetWidgetLog(): void {
  loggedNoSession = false;
}

/** Sends the change to the notebook's kernel. False when the message isn't one or there's no session to send it to. */
export function handleWidgetMessage(controller: ReverbController, notebookUri: vscode.Uri, m: unknown): boolean {
  const change = parseWidgetMessage(m);
  if (!change) return false;
  const session = controller.session(notebookUri);
  if (!session) {
    if (!loggedNoSession) {
      loggedNoSession = true;
      controller.log("Widget changes were dropped because this notebook has no kernel. If the folder is restricted, trust it; otherwise check the Reverb output for why the kernel didn't start.");
    }
    return false;
  }
  session.setWidget(change.widgetId, change.value, change.kind);
  return true;
}

export function registerWidgets(controller: ReverbController): vscode.Disposable {
  const messaging = vscode.notebooks.createRendererMessaging("reverb.outputs");
  const listener = messaging.onDidReceiveMessage((e) => void handleWidgetMessage(controller, e.editor.notebook.uri, e.message));
  return listener;
}
