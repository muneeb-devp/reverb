import * as assert from "node:assert";
import { cellStatusText, kernelStatusText } from "../../src/statusBar";

describe("cellStatusText", () => {
  it("says stale cells need a run", () => {
    assert.strictEqual(cellStatusText("stale", undefined), "Stale: run to update");
  });

  it("says disabled cells are disabled", () => {
    assert.strictEqual(cellStatusText("disabled", undefined), "Disabled");
  });

  it("shows the server's message for an upstream error, since it names the failing cell", () => {
    assert.strictEqual(cellStatusText("upstream-error", "Waiting on cell 'c1', which failed."), "Waiting on cell 'c1', which failed.");
  });

  it("explains an upstream error that has no message", () => {
    assert.match(cellStatusText("upstream-error", undefined)!, /upstream cell failed/);
  });

  it("shows nothing for cells that are up to date, running or failed on their own", () => {
    for (const s of ["idle", "queued", "running", "error"] as const) assert.strictEqual(cellStatusText(s, "x"), undefined);
    assert.strictEqual(cellStatusText(undefined, undefined), undefined);
  });
});

describe("kernelStatusText", () => {
  it("shows the state, with the server's message as the tooltip", () => {
    assert.deepStrictEqual(kernelStatusText("ready", "Restarted.", true), { text: "Reverb: ready", tooltip: "Restarted." });
    assert.deepStrictEqual(kernelStatusText("dead", "The kernel crashed.", true), { text: "Reverb: dead", tooltip: "The kernel crashed." });
  });

  it("has a tooltip even without a message", () => {
    assert.strictEqual(kernelStatusText("building", undefined, true).text, "Reverb: building");
    assert.ok(kernelStatusText("building", undefined, true).tooltip.length > 0);
  });

  it("says why there is no kernel in an untrusted folder", () => {
    const s = kernelStatusText(undefined, undefined, false);
    assert.strictEqual(s.text, "Reverb: restricted");
    assert.match(s.tooltip, /Trust this folder/);
  });

  it("says when the notebook isn't open in Reverb yet", () => {
    assert.strictEqual(kernelStatusText(undefined, undefined, true).text, "Reverb: not started");
  });
});
