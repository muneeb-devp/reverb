// The notebook renderer for Reverb's widget, chart and table outputs. It draws them with the
// browser editor's own components, so both editors look and behave the same.
import { useSyncExternalStore, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { ChartView } from "../../../frontend/src/ChartView";
import { TableView, type TableData } from "../../../frontend/src/Outputs";
import { MIME, type WidgetSpec } from "../../../frontend/src/protocol";
import { WidgetContext, WidgetView, type WidgetSender } from "../../../frontend/src/Widgets";
import { widgetSender } from "./messages";
import type { ActivationFunction, OutputItem } from "./rendererApi";
import css from "./renderer.css";
import { Roots } from "./roots";

// VS Code sets vscode-light, vscode-dark or vscode-high-contrast on the body and the theme id in
// data-vscode-theme-id, after updating the --vscode-* variables. Charts read their colours once,
// when drawn, so they redraw when either changes.
const themeListeners = new Set<() => void>();

function themeKey(): string {
  return `${document.body.className} ${document.body.dataset.vscodeThemeId ?? ""}`;
}

function subscribeTheme(listener: () => void) {
  themeListeners.add(listener);
  return () => {
    themeListeners.delete(listener);
  };
}

function watchTheme() {
  new MutationObserver(() => {
    for (const listener of themeListeners) listener();
  }).observe(document.body, { attributes: true, attributeFilter: ["class", "data-vscode-theme-id"] });
}

function injectStyles() {
  if (document.getElementById("reverb-outputs-style")) return;
  const style = document.createElement("style");
  style.id = "reverb-outputs-style";
  style.textContent = css;
  document.head.appendChild(style);
}

function Chart({ spec }: { spec: string }) {
  const key = useSyncExternalStore(subscribeTheme, themeKey);
  return <ChartView spec={spec} themeKey={key} />;
}

function view(item: OutputItem, send: WidgetSender | null): ReactNode {
  try {
    switch (item.mime) {
      case MIME.widget: {
        const spec = item.json() as WidgetSpec;
        return (
          <WidgetContext.Provider value={send}>
            <WidgetView key={spec.id} spec={spec} />
          </WidgetContext.Provider>
        );
      }
      case MIME.vegaLite:
        return <Chart spec={item.text()} />;
      case MIME.table:
        return <TableView table={item.json() as TableData} />;
      default:
        return <pre className="output-text">{`Reverb can't show ${item.mime} outputs.`}</pre>;
    }
  } catch (e) {
    return <pre className="output-text">{`This output couldn't be read: ${e instanceof Error ? e.message : String(e)}`}</pre>;
  }
}

export const activate: ActivationFunction = (context) => {
  injectStyles();
  watchTheme();
  const send = widgetSender(context.postMessage && ((message) => context.postMessage!(message)));
  const roots = new Roots<HTMLElement, Root>((el) => el.firstChild);
  return {
    renderOutputItem(item, element) {
      roots.rootFor(item.id, element, (el) => createRoot(el)).render(<div className="reverb-output">{view(item, send)}</div>);
    },
    disposeOutputItem(id) {
      roots.dispose(id);
    },
  };
};
