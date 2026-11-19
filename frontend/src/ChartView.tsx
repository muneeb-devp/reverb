import { useEffect, useRef, useState } from "react";
import { useTheme } from "./theme";

/** Vega-Lite colours from the page's own theme variables, so charts match light and dark. */
function chartConfig(): Record<string, unknown> {
  const css = getComputedStyle(document.documentElement);
  const v = (name: string) => css.getPropertyValue(name).trim();
  const text = v("--text");
  const muted = v("--text-muted");
  const border = v("--border");
  const font = v("--font-sans");
  return {
    background: null,
    font,
    view: { stroke: null },
    title: { color: text, fontWeight: 600, anchor: "start" },
    axisX: { labelAngle: 0 },
    axis: { labelColor: muted, titleColor: muted, gridColor: border, domainColor: border, tickColor: border, labelFont: font, titleFont: font },
    legend: { labelColor: muted, titleColor: muted, labelFont: font, titleFont: font },
    range: { category: { scheme: "tableau10" } },
  };
}

/**
 * A Vega-Lite chart. vega is loaded the first time a chart is shown. A host whose theme isn't
 * the page's light or dark setting (the VS Code renderer) passes `themeKey` to redraw the chart
 * when its theme changes.
 */
export function ChartView({ spec, themeKey }: { spec: string; themeKey?: string }) {
  const host = useRef<HTMLDivElement>(null);
  const { theme } = useTheme();
  const [problem, setProblem] = useState<string | null>(null);

  useEffect(() => {
    let parsed: unknown;
    try {
      parsed = JSON.parse(spec);
    } catch {
      setProblem("This chart's spec isn't valid JSON.");
      return;
    }
    setProblem(null);
    let cancelled = false;
    let finalize: (() => void) | undefined;
    import("vega-embed")
      .then(({ default: embed }) => {
        if (cancelled || !host.current) return;
        // No actions menu: one of its items sends the spec to an outside site.
        return embed(host.current, parsed as never, { actions: false, renderer: "svg", config: chartConfig() as never });
      })
      .then((result) => {
        if (!result) return;
        if (cancelled) result.finalize();
        else finalize = () => result.finalize();
      })
      .catch((e: unknown) => {
        if (!cancelled) setProblem(`The chart couldn't be drawn: ${e instanceof Error ? e.message : String(e)}`);
      });
    return () => {
      cancelled = true;
      finalize?.();
    };
  }, [spec, theme, themeKey]);

  return (
    <div className="output-chart" data-testid="chart">
      {problem ? <pre className="output-text">{problem}</pre> : <div ref={host} className="chart-host" />}
    </div>
  );
}
