import { useMemo } from "react";
import { MIME, type Output, type WidgetSpec } from "./protocol";
import { ChartView } from "./ChartView";
import { WidgetView } from "./Widgets";
import { renderMarkdown } from "./markdown";

export interface TableData {
  columns: string[];
  rows: string[][];
  total: number;
}

interface ErrorData {
  type: string;
  message: string;
  location?: string;
  stackTrace?: string;
}

function parse<T>(json: string | undefined): T | null {
  if (json === undefined) return null;
  try {
    return JSON.parse(json) as T;
  } catch {
    return null;
  }
}

export function Outputs({ outputs }: { outputs: Output[] }) {
  return (
    <div className="outputs" data-testid="outputs">
      {outputs.map((o, i) => (
        <OutputView key={widgetKey(o) ?? i} output={o} />
      ))}
    </div>
  );
}

function widgetKey(o: Output): string | undefined {
  const json = o.data?.[MIME.widget];
  if (!json) return undefined;
  return parse<WidgetSpec>(json)?.id;
}

function OutputView({ output }: { output: Output }) {
  if (output.stream) {
    return <pre className={`output stream ${output.stream}`}>{output.text}</pre>;
  }
  const data = output.data ?? {};
  return (
    <div className="output display">
      {output.title && <div className="output-title">{output.title}</div>}
      <Rich data={data} />
    </div>
  );
}

function Rich({ data }: { data: Record<string, string> }) {
  if (data[MIME.widget]) {
    const w = parse<WidgetSpec>(data[MIME.widget]);
    if (w) return <WidgetView spec={w} />;
  }
  if (data[MIME.error]) {
    const e = parse<ErrorData>(data[MIME.error]);
    if (e) return <ErrorView error={e} />;
  }
  if (data[MIME.vegaLite]) return <ChartView spec={data[MIME.vegaLite]} />;
  if (data[MIME.table]) {
    const t = parse<TableData>(data[MIME.table]);
    if (t) return <TableView table={t} />;
  }
  if (data[MIME.html]) return <div className="output-html" dangerouslySetInnerHTML={{ __html: data[MIME.html] }} />;
  if (data[MIME.markdown]) return <MarkdownView text={data[MIME.markdown]} />;
  // Images go through <img>, so SVG scripts can't run.
  if (data[MIME.svg]) return <img className="output-image" alt="" src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(data[MIME.svg])}`} />;
  if (data[MIME.png]) return <img className="output-image" alt="" src={`data:image/png;base64,${data[MIME.png]}`} />;
  if (data[MIME.jpeg]) return <img className="output-image" alt="" src={`data:image/jpeg;base64,${data[MIME.jpeg]}`} />;
  return <pre className="output-text">{data[MIME.plain] ?? ""}</pre>;
}

export function MarkdownView({ text }: { text: string }) {
  const html = useMemo(() => renderMarkdown(text), [text]);
  return <div className="markdown" dangerouslySetInnerHTML={{ __html: html }} />;
}

export function TableView({ table }: { table: TableData }) {
  return (
    <div className="table-wrap">
      <table className="output-table">
        <thead>
          <tr>
            {table.columns.map((c, i) => (
              <th key={i}>{c}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {table.rows.map((row, r) => (
            <tr key={r}>
              {row.map((cell, i) => (
                <td key={i}>{cell}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      <div className="table-note">
        {table.total > table.rows.length
          ? `Showing ${table.rows.length.toLocaleString()} of ${table.total.toLocaleString()} rows`
          : `${table.total.toLocaleString()} ${table.total === 1 ? "row" : "rows"}`}
      </div>
    </div>
  );
}

function ErrorView({ error }: { error: ErrorData }) {
  const shortType = error.type.split(".").pop();
  return (
    <div className="output-error" role="alert">
      <div className="error-head">
        <strong>{shortType}</strong>: {error.message}
      </div>
      {error.location && <div className="error-location">at {error.location}</div>}
      {error.stackTrace && (
        <details>
          <summary>Stack trace</summary>
          <pre>{error.stackTrace}</pre>
        </details>
      )}
    </div>
  );
}
