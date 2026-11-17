import * as assert from "node:assert";
import { correctionOrder, isReload, notebookLost, saveDelay, TYPING_SAVE_DELAY_MS, type ChangeSummary } from "../../src/notebookChanges";
import type { CellKindName, PartCell } from "../../src/protocol";

describe("notebookLost", () => {
  // The texts StdioServer sends when it has no host for a notebook.
  it("is true for the errors that mean reverb has no host for the notebook", () => {
    assert.strictEqual(notebookLost("The notebook /n/a.cs isn't open in Reverb, so its 'notebook.sync' message was dropped. Reopen the notebook to keep working on it."), true);
    assert.strictEqual(notebookLost("/n/a.cs couldn't be opened: The kernel didn't start."), true);
  });

  it("is false for other errors", () => {
    assert.strictEqual(notebookLost("The cells sent for /n/a.cs couldn't be used: a cell has no kind."), false);
    assert.strictEqual(notebookLost("The notebook's cells couldn't be updated: bad id."), false);
  });
});

function change(c: Partial<ChangeSummary>): ChangeSummary {
  return { content: [], cellMetadata: false, text: false, notebookMetadata: false, ...c };
}

function part(kind: CellKindName, id?: string): PartCell {
  return { kind, id: id ?? null, source: "", attributes: [] };
}

describe("isReload", () => {
  // What VS Code 1.146 sent when c1 changed on disk: only c1 replaced, and the notebook's metadata
  // set again even though it was the same.
  const oneCellChanged = change({ content: [{ start: 1, end: 2, removed: 1, added: 1 }], cellMetadata: true, notebookMetadata: true });

  it("is a change that sets the notebook's metadata", () => {
    assert.strictEqual(isReload(oneCellChanged, false), true);
  });

  it("is a reload when only the lines above the cells changed", () => {
    assert.strictEqual(isReload(change({ cellMetadata: true, notebookMetadata: true }), false), true);
  });

  it("is not a reload when the session wrote the notebook's metadata itself", () => {
    assert.strictEqual(isReload(change({ notebookMetadata: true }), true), false);
  });

  it("is not a reload when the notebook's metadata wasn't set, even if every cell was replaced", () => {
    assert.strictEqual(isReload(change({ content: [{ start: 0, end: 3, removed: 3, added: 3 }] }), false), false);
    assert.strictEqual(isReload(change({ text: true }), false), false);
  });
});

describe("saveDelay", () => {
  it("saves at once after cells are added, removed or moved", () => {
    assert.strictEqual(saveDelay(change({ content: [{ start: 2, end: 2, removed: 0, added: 1 }] })), 0);
  });

  it("saves at once after a package or lazy change, which changes the notebook's metadata", () => {
    assert.strictEqual(saveDelay(change({ notebookMetadata: true })), 0);
  });

  it("saves at once after a cell's Reverb metadata changes", () => {
    assert.strictEqual(saveDelay(change({ cellMetadata: true })), 0);
  });

  it("waits for typing to stop", () => {
    assert.strictEqual(saveDelay(change({ text: true })), TYPING_SAVE_DELAY_MS);
    assert.strictEqual(TYPING_SAVE_DELAY_MS, 1000);
  });

  it("doesn't save for outputs and run results, which aren't in the file", () => {
    assert.strictEqual(saveDelay(change({})), undefined);
  });
});

describe("correctionOrder", () => {
  it("is the same order when the server only changed ids", () => {
    const sent: CellKindName[] = ["setup", "code", "code", "types"];
    assert.deepStrictEqual(correctionOrder(sent, [part("setup"), part("code", "a"), part("code", "b"), part("types")]), [0, 1, 2, 3]);
  });

  it("finds the setup cell the server moved to the top", () => {
    assert.deepStrictEqual(correctionOrder(["code", "markdown", "setup"], [part("setup"), part("code", "a"), part("markdown", "m")]), [2, 0, 1]);
  });

  it("finds the types cell the server moved to the end", () => {
    assert.deepStrictEqual(correctionOrder(["setup", "types", "code"], [part("setup"), part("code", "a"), part("types")]), [0, 2, 1]);
  });

  it("follows the server in turning a second setup or types cell into code", () => {
    const sent: CellKindName[] = ["setup", "setup", "types", "types"];
    assert.deepStrictEqual(correctionOrder(sent, [part("setup"), part("code", "a"), part("code", "b"), part("types")]), [0, 1, 3, 2]);
  });

  it("gives up when the cell count changed since the list was sent", () => {
    assert.strictEqual(correctionOrder(["setup", "code"], [part("setup"), part("code", "a"), part("code", "b")]), undefined);
  });

  it("gives up when the kinds don't line up", () => {
    assert.strictEqual(correctionOrder(["code", "markdown"], [part("code", "a"), part("code", "b")]), undefined);
  });
});
