import * as assert from "node:assert";
import { appendStream, errorItem, toOutputItems } from "../../src/outputs";

describe("outputs", () => {
  it("html is preferred over plain text", () => {
    assert.deepEqual(toOutputItems({ data: { "text/plain": "t", "text/html": "<b>t</b>" } }).map((i) => i.mime), ["text/html", "text/plain"]);
  });

  it("keeps the renderable mimes, richest first", () => {
    const items = toOutputItems({
      data: {
        "text/plain": "p",
        "image/svg+xml": "<svg/>",
        "text/markdown": "*m*",
        "text/html": "<i>h</i>",
        "application/x-unknown": "?",
      },
    });
    assert.deepEqual(items, [
      { mime: "text/html", data: "<i>h</i>" },
      { mime: "text/markdown", data: "*m*" },
      { mime: "image/svg+xml", data: "<svg/>" },
      { mime: "text/plain", data: "p" },
    ]);
  });

  it("stream text becomes a stdout or stderr item", () => {
    assert.deepEqual(toOutputItems({ stream: "stdout", text: "11\n" }), [{ mime: "application/vnd.code.notebook.stdout", data: "11\n" }]);
    assert.deepEqual(toOutputItems({ stream: "stderr", text: "oops\n" }), [{ mime: "application/vnd.code.notebook.stderr", data: "oops\n" }]);
  });

  it("a reverb error becomes an error item with the cell and line", () => {
    const output = {
      data: {
        "text/plain": "InvalidOperationException: boom\n  at cell 'c2', line 3",
        "application/vnd.reverb.error+json": JSON.stringify({
          type: "System.InvalidOperationException",
          message: "boom",
          location: "cell 'c2', line 3",
          stackTrace: "   at Submission#3.<Factory>()",
        }),
      },
    };
    const e = errorItem(output)!;
    assert.strictEqual(e.name, "InvalidOperationException");
    assert.strictEqual(e.message, "boom\n  at cell 'c2', line 3");
    assert.strictEqual(e.stack, "InvalidOperationException: boom\n  at cell 'c2', line 3\n   at Submission#3.<Factory>()");
    // The error item replaces the output's other forms.
    assert.deepEqual(toOutputItems(output), []);
  });

  it("an error without a location or stack still has its message", () => {
    const e = errorItem({ data: { "application/vnd.reverb.error+json": JSON.stringify({ type: "Oops", message: "bad" }) } })!;
    assert.deepEqual(e, { name: "Oops", message: "bad", stack: "Oops: bad" });
  });

  it("an error that isn't JSON falls back to its text", () => {
    const output = { data: { "text/plain": "Broken: x", "application/vnd.reverb.error+json": "{not json" } };
    assert.strictEqual(errorItem(output), undefined);
    assert.deepEqual(toOutputItems(output).map((i) => i.mime), ["text/plain"]);
  });

  it("is not an error without the error mime", () => {
    assert.strictEqual(errorItem({ data: { "text/plain": "x" } }), undefined);
    assert.strictEqual(errorItem({ stream: "stdout", text: "x" }), undefined);
  });

  it("a widget output is only its widget item, so the Reverb renderer draws it", () => {
    const json = JSON.stringify({ kind: "slider", label: "n", id: "w1", value: 7, min: 0, max: 10 });
    const items = toOutputItems({ data: { "application/vnd.reverb.widget+json": json, "text/html": "<input>", "text/plain": "[slider n = 7]" } });
    assert.deepEqual(items, [{ mime: "application/vnd.reverb.widget+json", data: json }]);
  });

  it("a chart output is only its Vega-Lite item", () => {
    const json = JSON.stringify({ mark: "bar" });
    const items = toOutputItems({ data: { "application/vnd.vegalite.v5+json": json, "text/html": "<div>chart</div>", "text/plain": "chart" } });
    assert.deepEqual(items, [{ mime: "application/vnd.vegalite.v5+json", data: json }]);
  });

  it("a table output is only its table item", () => {
    const json = JSON.stringify({ columns: ["a"], rows: [["1"]], total: 1 });
    const items = toOutputItems({ data: { "application/vnd.reverb.table+json": json, "text/html": "<table></table>", "text/plain": "table" } });
    assert.deepEqual(items, [{ mime: "application/vnd.reverb.table+json", data: json }]);
  });

  it("the object mime is still dropped, its text kept", () => {
    assert.deepEqual(toOutputItems({ data: { "application/vnd.reverb.object+json": "{}", "text/plain": "obj" } }).map((i) => i.mime), ["text/plain"]);
  });

  it("png is base64-decoded", () => {
    const bytes = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0xff]);
    const items = toOutputItems({ data: { "image/png": Buffer.from(bytes).toString("base64"), "text/plain": "[image/png image, 10 bytes]" } });
    assert.deepEqual(items.map((i) => i.mime), ["image/png", "text/plain"]);
    assert.ok(items[0].data instanceof Uint8Array);
    assert.deepEqual([...(items[0].data as Uint8Array)], [...bytes]);
  });

  it("jpeg is base64-decoded", () => {
    const items = toOutputItems({ data: { "image/jpeg": Buffer.from([0xff, 0xd8, 0xff, 0x01]).toString("base64") } });
    assert.deepEqual([...(items[0].data as Uint8Array)], [0xff, 0xd8, 0xff, 0x01]);
  });

  it("an output with nothing VS Code can show gives no items", () => {
    assert.deepEqual(toOutputItems({ data: { "application/vnd.reverb.object+json": "{}" } }), []);
    assert.deepEqual(toOutputItems({}), []);
  });

  it("appendStream adds to the last output of the same stream, as the server does", () => {
    let outputs = appendStream([], "stdout", "a");
    outputs = appendStream(outputs, "stdout", "b");
    assert.deepEqual(outputs, [{ stream: "stdout", text: "ab" }]);
    outputs = appendStream(outputs, "stderr", "c");
    outputs = appendStream(outputs, "stdout", "d");
    assert.deepEqual(outputs, [
      { stream: "stdout", text: "ab" },
      { stream: "stderr", text: "c" },
      { stream: "stdout", text: "d" },
    ]);
  });

  it("appendStream doesn't change the list it was given", () => {
    const before = [{ stream: "stdout" as const, text: "a" }];
    appendStream(before, "stdout", "b");
    assert.deepEqual(before, [{ stream: "stdout", text: "a" }]);
  });
});
