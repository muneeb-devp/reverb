import * as assert from "node:assert";
import { parseWidgetMessage } from "../../src/widgetMessage";

describe("parseWidgetMessage", () => {
  for (const kind of ["slider", "dropdown", "text", "checkbox", "number", "button"]) {
    it(`accepts ${kind}`, () => {
      assert.deepStrictEqual(parseWidgetMessage({ type: "widget.setValue", widgetId: "w1", value: 3, kind }), { widgetId: "w1", value: 3, kind });
    });
  }

  it("keeps a missing value as undefined", () => {
    assert.deepStrictEqual(parseWidgetMessage({ type: "widget.setValue", widgetId: "w1", kind: "button" }), { widgetId: "w1", value: undefined, kind: "button" });
  });

  it("rejects what isn't a widget change", () => {
    const bad: unknown[] = [
      undefined,
      null,
      "widget.setValue",
      42,
      [],
      {},
      { type: "other", widgetId: "w1", value: 1, kind: "slider" },
      { type: "widget.setValue", value: 1, kind: "slider" },
      { type: "widget.setValue", widgetId: 5, value: 1, kind: "slider" },
      { type: "widget.setValue", widgetId: "", value: 1, kind: "slider" },
      { type: "widget.setValue", widgetId: "w1", value: 1, kind: "knob" },
      { type: "widget.setValue", widgetId: "w1", value: 1 },
    ];
    for (const m of bad) assert.strictEqual(parseWidgetMessage(m), undefined, JSON.stringify(m));
  });
});
