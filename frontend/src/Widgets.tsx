import { createContext, useContext, useEffect, useRef, useState } from "react";
import type { WidgetKind, WidgetSpec } from "./protocol";

export type WidgetSender = (id: string, value: unknown, kind: WidgetKind) => void;

/** How widgets send changes. Provided by the app; absent (no-op) in static views. */
export const WidgetContext = createContext<WidgetSender | null>(null);

/**
 * The value shown in a control. It follows what the kernel reports, except right after the user
 * changed it: until the kernel has caught up (or a moment has passed), the user's value wins, so a
 * slider doesn't jump back to an older position mid-drag.
 */
function useLocalValue<T>(reported: T): [T, (v: T) => void] {
  const [value, setValue] = useState(reported);
  const mine = useRef<{ value: T; until: number } | null>(null);
  useEffect(() => {
    const m = mine.current;
    if (m && reported !== m.value && Date.now() < m.until) return;
    mine.current = null;
    setValue(reported);
  }, [reported]);
  const set = (v: T) => {
    mine.current = { value: v, until: Date.now() + 3000 };
    setValue(v);
  };
  return [value, set];
}

/** Calls `fn` after the user pauses, keeping only the last call. */
function useDebounced<A extends unknown[]>(fn: (...args: A) => void, ms: number) {
  const timer = useRef<number | undefined>(undefined);
  const latest = useRef(fn);
  latest.current = fn;
  useEffect(() => () => window.clearTimeout(timer.current), []);
  return (...args: A) => {
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => latest.current(...args), ms);
  };
}

function formatNumber(n: number | null | undefined) {
  if (n === null || n === undefined) return "";
  return Number.isInteger(n) ? String(n) : String(Math.round(n * 1e6) / 1e6);
}

export function WidgetView({ spec }: { spec: WidgetSpec }) {
  const send = useContext(WidgetContext);
  const live = send !== null && spec.id !== undefined;
  const emit = (value: unknown) => {
    if (live) send(spec.id!, value, spec.kind);
  };
  const props = { spec, live, emit };
  switch (spec.kind) {
    case "slider":
      return <SliderView {...props} />;
    case "dropdown":
      return <DropdownView {...props} />;
    case "text":
      return <TextView {...props} />;
    case "textarea":
      return <TextAreaView {...props} />;
    case "checkbox":
      return <CheckboxView {...props} />;
    case "number":
      return <NumberView {...props} />;
    case "button":
      return <ButtonView {...props} />;
    default:
      return <pre className="output-text">{`[${spec.kind} ${spec.label}]`}</pre>;
  }
}

interface ControlProps {
  spec: WidgetSpec;
  live: boolean;
  emit: (value: unknown) => void;
}

function SliderView({ spec, live, emit }: ControlProps) {
  const [value, setValue] = useLocalValue(Number(spec.value));
  return (
    <label className="widget widget-slider" data-widget-id={spec.id}>
      <span className="widget-label">{spec.label}</span>
      <input
        type="range"
        min={spec.min}
        max={spec.max}
        step={spec.step}
        value={value}
        disabled={!live}
        aria-label={spec.label}
        onChange={(e) => {
          const v = Number(e.target.value);
          setValue(v);
          emit(v);
        }}
      />
      <output className="widget-value">{formatNumber(value)}</output>
    </label>
  );
}

function DropdownView({ spec, live, emit }: ControlProps) {
  const [index, setIndex] = useLocalValue(spec.index ?? 0);
  return (
    <label className="widget widget-dropdown" data-widget-id={spec.id}>
      <span className="widget-label">{spec.label}</span>
      <select
        value={index}
        disabled={!live}
        aria-label={spec.label}
        onChange={(e) => {
          const i = Number(e.target.value);
          setIndex(i);
          emit(i);
        }}
      >
        {(spec.options ?? []).map((o, i) => (
          <option key={i} value={i}>
            {o}
          </option>
        ))}
      </select>
    </label>
  );
}

function TextView({ spec, live, emit }: ControlProps) {
  const [text, setText] = useLocalValue(String(spec.value ?? ""));
  const later = useDebounced(emit, 250);
  return (
    <label className="widget widget-text" data-widget-id={spec.id}>
      <span className="widget-label">{spec.label}</span>
      <input
        type="text"
        value={text}
        disabled={!live}
        aria-label={spec.label}
        spellCheck={false}
        onChange={(e) => {
          setText(e.target.value);
          later(e.target.value);
        }}
        onKeyDown={(e) => {
          if (e.key === "Enter") emit(text);
        }}
      />
    </label>
  );
}

function TextAreaView({ spec, live, emit }: ControlProps) {
  const [text, setText] = useLocalValue(String(spec.value ?? ""));
  const later = useDebounced(emit, 400);
  return (
    <label className="widget widget-textarea" data-widget-id={spec.id}>
      <span className="widget-label">{spec.label}</span>
      <textarea
        rows={spec.rows ?? 4}
        value={text}
        disabled={!live}
        aria-label={spec.label}
        spellCheck={false}
        onChange={(e) => {
          setText(e.target.value);
          later(e.target.value);
        }}
        onKeyDown={(e) => {
          if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) {
            e.preventDefault();
            emit(text);
          }
        }}
      />
    </label>
  );
}

function CheckboxView({ spec, live, emit }: ControlProps) {
  const [checked, setChecked] = useLocalValue(Boolean(spec.value));
  return (
    <label className="widget widget-checkbox" data-widget-id={spec.id}>
      <input
        type="checkbox"
        checked={checked}
        disabled={!live}
        onChange={(e) => {
          setChecked(e.target.checked);
          emit(e.target.checked);
        }}
      />
      <span className="widget-label">{spec.label}</span>
    </label>
  );
}

function NumberView({ spec, live, emit }: ControlProps) {
  const [text, setText] = useLocalValue(formatNumber(spec.value as number));
  const later = useDebounced(emit, 250);
  const n = Number(text);
  const invalid = text.trim() === "" || Number.isNaN(n) || (spec.min !== undefined && n < spec.min) || (spec.max !== undefined && n > spec.max);
  return (
    <label className={`widget widget-number${invalid ? " invalid" : ""}`} data-widget-id={spec.id}>
      <span className="widget-label">{spec.label}</span>
      <input
        type="number"
        min={spec.min}
        max={spec.max}
        step={spec.step ?? "any"}
        value={text}
        disabled={!live}
        aria-label={spec.label}
        onChange={(e) => {
          setText(e.target.value);
          const v = Number(e.target.value);
          const ok = e.target.value.trim() !== "" && !Number.isNaN(v) && (spec.min === undefined || v >= spec.min) && (spec.max === undefined || v <= spec.max);
          if (ok) later(v);
        }}
      />
      {invalid && spec.min !== undefined && spec.max !== undefined && (
        <span className="widget-hint">
          {formatNumber(spec.min)} to {formatNumber(spec.max)}
        </span>
      )}
    </label>
  );
}

function ButtonView({ spec, live, emit }: ControlProps) {
  return (
    <div className="widget widget-button" data-widget-id={spec.id}>
      <button className="primary" disabled={!live} onClick={() => emit(true)}>
        {spec.label}
      </button>
    </div>
  );
}
