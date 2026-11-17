import * as assert from "node:assert";
import { ExecutionTracker } from "../../src/executions";

describe("ExecutionTracker", () => {
  it("queued then running then idle creates, starts and ends with success", () => {
    const t = new ExecutionTracker();
    assert.deepEqual(t.onStatus("a", "queued", 0, 1).map((a) => a.kind), ["create"]);
    assert.deepEqual(t.onStatus("a", "running", 1, 2), [{ kind: "start", cellId: "a", order: 1, startTime: 2 }]);
    assert.deepEqual(t.onStatus("a", "idle", 1, 40), [{ kind: "end", cellId: "a", success: true, endTime: 40 }]);
  });

  it("running without queued creates and starts", () => {
    const t = new ExecutionTracker();
    assert.deepEqual(t.onStatus("a", "running", 3, 5), [
      { kind: "create", cellId: "a" },
      { kind: "start", cellId: "a", order: 3, startTime: 5 },
    ]);
  });

  it("error ends with failure", () => {
    const t = new ExecutionTracker();
    t.onStatus("a", "running", 1, 1);
    assert.deepEqual(t.onStatus("a", "error", 1, 9), [{ kind: "end", cellId: "a", success: false, endTime: 9 }]);
  });

  it("upstream-error ends with failure", () => {
    const t = new ExecutionTracker();
    t.onStatus("b", "queued", 0, 1);
    assert.deepEqual(t.onStatus("b", "upstream-error", 0, 4), [{ kind: "end", cellId: "b", success: false, endTime: 4 }]);
  });

  it("stale or disabled ends an open execution without success", () => {
    const t = new ExecutionTracker();
    t.onStatus("a", "running", 1, 1);
    t.onStatus("b", "queued", 0, 1);
    assert.deepEqual(t.onStatus("a", "stale", 1, 13), [{ kind: "end", cellId: "a", success: false }]);
    assert.deepEqual(t.onStatus("b", "disabled", 0, 13), [{ kind: "end", cellId: "b", success: false }]);
  });

  it("stale with nothing open does nothing", () => {
    const t = new ExecutionTracker();
    assert.deepEqual(t.onStatus("a", "stale", 0, 1), []);
    assert.deepEqual(t.onStatus("a", "disabled", 0, 1), []);
  });

  it("idle or error with nothing open does nothing", () => {
    // A late status for an execution that endAll already ended.
    const t = new ExecutionTracker();
    assert.deepEqual(t.onStatus("a", "idle", 1, 4), []);
    assert.deepEqual(t.onStatus("a", "error", 1, 4), []);
  });

  it("queued again while open keeps the one execution", () => {
    const t = new ExecutionTracker();
    t.onStatus("a", "queued", 0, 1);
    assert.deepEqual(t.onStatus("a", "queued", 0, 2), []);
    t.onStatus("a", "running", 1, 3);
    assert.deepEqual(t.onStatus("a", "queued", 1, 4), []);
  });

  it("running again while started updates the order instead of starting twice", () => {
    const t = new ExecutionTracker();
    t.onStatus("a", "running", 1, 1);
    t.onStatus("a", "queued", 1, 2);
    assert.deepEqual(t.onStatus("a", "running", 2, 3), [{ kind: "order", cellId: "a", order: 2 }]);
    assert.deepEqual(t.onStatus("a", "idle", 2, 4), [{ kind: "end", cellId: "a", success: true, endTime: 4 }]);
  });

  it("forget ends an open execution without success and drops it", () => {
    const t = new ExecutionTracker();
    t.onStatus("a", "queued", 0, 1);
    t.onStatus("b", "running", 1, 1);
    assert.deepEqual(t.forget("a"), [{ kind: "end", cellId: "a", success: false }]);
    assert.strictEqual(t.state("a"), undefined);
    assert.deepEqual(t.forget("b"), [{ kind: "end", cellId: "b", success: false }]);
    assert.deepEqual(t.endAll(), []);
  });

  it("forget with nothing open does nothing", () => {
    const t = new ExecutionTracker();
    assert.deepEqual(t.forget("a"), []);
  });

  it("a forgotten cell gets a new execution when it's queued again", () => {
    const t = new ExecutionTracker();
    t.onStatus("a", "queued", 0, 1);
    t.forget("a");
    assert.deepEqual(t.onStatus("a", "queued", 0, 2), [{ kind: "create", cellId: "a" }]);
  });

  it("a cell interrupted and requeued gets a new execution", () => {
    const t = new ExecutionTracker();
    t.onStatus("a", "running", 1, 1);
    assert.deepEqual(t.onStatus("a", "stale", 1, 3).map((a) => a.kind), ["end"]);
    assert.deepEqual(t.onStatus("a", "queued", 1, 4).map((a) => a.kind), ["create"]);
    assert.deepEqual(t.onStatus("a", "running", 2, 5), [{ kind: "start", cellId: "a", order: 2, startTime: 5 }]);
  });

  it("endAll fails every open execution", () => {
    const t = new ExecutionTracker();
    t.onStatus("a", "running", 1, 1);
    t.onStatus("b", "queued", 0, 1);
    t.onStatus("c", "running", 1, 1);
    t.onStatus("c", "idle", 1, 2);
    assert.deepEqual(t.endAll(), [
      { kind: "end", cellId: "a", success: false },
      { kind: "end", cellId: "b", success: false },
    ]);
    assert.deepEqual(t.endAll(), []);
    assert.deepEqual(t.onStatus("a", "idle", 1, 4), []);
  });

  it("reports whether a cell's execution has started", () => {
    const t = new ExecutionTracker();
    assert.strictEqual(t.state("a"), undefined);
    t.onStatus("a", "queued", 0, 1);
    assert.strictEqual(t.state("a"), "created");
    t.onStatus("a", "running", 1, 2);
    assert.strictEqual(t.state("a"), "started");
    t.onStatus("a", "idle", 1, 3);
    assert.strictEqual(t.state("a"), undefined);
  });
});
