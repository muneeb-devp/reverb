import * as assert from "node:assert";
import { cellsToParts, partsToCells, readNotebookMetadata, type ReverbNotebookMetadata } from "../../src/metadata";
import type { NotebookPartsData, RawCellData } from "../../src/protocol";

function raw(kind: RawCellData["kind"], id: string | null, source: string, markerLine: string, line: number): RawCellData {
  return {
    markerLine,
    leadingPad: "",
    content: source + "\r\n",
    trailingPad: "\r\n",
    wasLast: false,
    kind,
    id,
    attributes: [],
    source,
    markerLineNumber: line,
  };
}

// The shape cellsToParts produces: nulls where the server would leave a field out.
const parts: NotebookPartsData = {
  preamble: [
    { text: "#!/usr/bin/env dotnet", terminator: "\r\n" },
    { text: "// reverb-notebook v1 lazy", terminator: "\r\n" },
    { text: "", terminator: "\r\n" },
  ],
  newline: "\r\n",
  bom: true,
  cells: [
    { kind: "setup", id: null, source: "using Reverb;", attributes: [], raw: raw("setup", null, "using Reverb;", "// %% setup\r\n", 4) },
    {
      kind: "code",
      id: "a",
      source: "var a = 1;\nvar b = 2;",
      attributes: [{ key: "hide_code" }, { key: "title", value: "First" }],
      raw: null,
    },
    { kind: "markdown", id: "m", source: "# Notes", attributes: [], raw: raw("markdown", "m", "/*\n# Notes\n*/", "// %% [markdown] id=m\r\n", 12) },
    { kind: "types", id: "types", source: "record P(int X);", attributes: [], raw: null },
  ],
};

const meta: ReverbNotebookMetadata = { preamble: [], newline: "\n", bom: false };

describe("metadata", () => {
  it("partsToCells then cellsToParts is identity", () => {
    const { cells, metadata } = partsToCells(parts);
    assert.deepStrictEqual(cellsToParts(cells, metadata), parts);
  });

  it("survives a JSON round trip, as VS Code stores metadata", () => {
    const { cells, metadata } = JSON.parse(JSON.stringify(partsToCells(parts)));
    assert.deepStrictEqual(cellsToParts(cells, metadata), parts);
  });

  it("puts the notebook's preamble, newline and BOM in notebook metadata", () => {
    const { metadata } = partsToCells(parts);
    assert.deepStrictEqual(metadata, { preamble: parts.preamble, newline: "\r\n", bom: true });
  });

  it("a pasted cell without metadata becomes a code cell without an id", () => {
    const result = cellsToParts([{ kind: "code", source: "x", metadata: {} }], meta);
    assert.deepStrictEqual(result.cells[0], { kind: "code", id: null, source: "x", attributes: [], raw: null });
  });

  it("sends cell text with \\n even when VS Code gave the cell \\r\\n", () => {
    const result = cellsToParts([{ kind: "code", source: "var a = 1;\r\nvar b = 2;\rvar c = 3;", metadata: {} }], meta);
    assert.strictEqual(result.cells[0].source, "var a = 1;\nvar b = 2;\nvar c = 3;");
  });

  it("markdown cells keep kind markdown", () => {
    const { cells } = partsToCells(parts);
    assert.strictEqual(cells[2].kind, "markup");
    assert.strictEqual(cells[2].metadata.reverb?.kind, "markdown");
    assert.strictEqual(cellsToParts(cells, meta).cells[2].kind, "markdown");
  });

  it("setup and types cells are code cells that keep their kind", () => {
    const { cells } = partsToCells(parts);
    assert.deepStrictEqual(
      cells.map((c) => [c.kind, c.metadata.reverb?.kind]),
      [["code", "setup"], ["code", "code"], ["markup", "markdown"], ["code", "types"]],
    );
  });

  it("a pasted markdown cell without metadata is saved as markdown", () => {
    const result = cellsToParts([{ kind: "markup", source: "# Hi", metadata: {} }], meta);
    assert.deepStrictEqual(result.cells[0], { kind: "markdown", id: null, source: "# Hi", attributes: [], raw: null });
  });

  it("a cell switched between code and markdown in VS Code is saved as its new kind", () => {
    const { cells } = partsToCells(parts);
    const switched = [
      { ...cells[1], kind: "markup" as const },
      { ...cells[2], kind: "code" as const },
    ];
    assert.deepStrictEqual(cellsToParts(switched, meta).cells.map((c) => [c.id, c.kind]), [["a", "markdown"], ["m", "code"]]);
  });

  it("leaves out fields the server didn't send rather than storing nulls", () => {
    const fromServer = {
      preamble: [],
      newline: "\n",
      bom: false,
      cells: [{ kind: "setup", source: "using Reverb;", attributes: [{ key: "hide_code" }] }],
    } as NotebookPartsData;
    const { cells } = partsToCells(fromServer);
    assert.deepStrictEqual(cells[0].metadata, { reverb: { kind: "setup", attrs: [{ key: "hide_code" }] } });
    assert.deepStrictEqual(cellsToParts(cells, meta).cells[0], {
      kind: "setup",
      id: null,
      source: "using Reverb;",
      attributes: [{ key: "hide_code" }],
      raw: null,
    });
  });

  it("readNotebookMetadata falls back to an empty preamble and LF when the notebook has none", () => {
    assert.deepStrictEqual(readNotebookMetadata(undefined), { preamble: [], newline: "\n", bom: false });
    assert.deepStrictEqual(readNotebookMetadata({ other: 1 }), { preamble: [], newline: "\n", bom: false });
    const stored = { reverb: { preamble: [{ text: "// reverb-notebook v1", terminator: "\r\n" }], newline: "\r\n", bom: true } };
    assert.deepStrictEqual(readNotebookMetadata(stored), stored.reverb);
  });
});
