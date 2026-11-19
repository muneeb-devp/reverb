import * as assert from "node:assert";
import { planFixEdits, type CurrentCell } from "../../src/fixEdits";
import type { CellKindName, PartCell } from "../../src/protocol";

function part(kind: CellKindName, source: string, id?: string): PartCell {
  return { kind, id: id ?? null, source, attributes: [] };
}

function cell(kind: CellKindName, source: string, id?: string): CurrentCell {
  return { kind, id, source };
}

const SETUP = "using Reverb;\nusing static Reverb.Notebook;";
const C2 = "using System.Text;\nvar sb = new StringBuilder().Append(a);";
const C2_FIXED = "var sb = new StringBuilder().Append(a);";

describe("planFixEdits", () => {
  it("replaces the text of both cells when a using moves into an existing setup cell", () => {
    const current = [cell("setup", SETUP), cell("code", "var a = 2;", "c1"), cell("code", C2, "c2")];
    const fixed = [part("setup", `${SETUP}\nusing System.Text;`), part("code", "var a = 2;", "c1"), part("code", C2_FIXED, "c2")];
    assert.deepStrictEqual(planFixEdits(current, fixed), {
      replace: [
        { index: 0, source: `${SETUP}\nusing System.Text;` },
        { index: 2, source: C2_FIXED },
      ],
      insert: [],
    });
  });

  it("inserts a setup cell at the top when the notebook has none", () => {
    const current = [cell("code", "var a = 2;", "c1"), cell("code", C2, "c2")];
    const setup = part("setup", "using System.Text;");
    const fixed = [setup, part("code", "var a = 2;", "c1"), part("code", C2_FIXED, "c2")];
    assert.deepStrictEqual(planFixEdits(current, fixed), {
      replace: [{ index: 1, source: C2_FIXED }],
      insert: [{ index: 0, cell: setup }],
    });
  });

  it("adds a types cell at the end when the notebook has none", () => {
    const current = [cell("setup", SETUP), cell("code", "var p = new { X = 1 };", "c1"), cell("code", "Console.WriteLine(p.X);", "c2")];
    const types = part("types", "record P1(int X);");
    const fixed = [part("setup", SETUP), part("code", "var p = new P1(1);", "c1"), part("code", "Console.WriteLine(p.X);", "c2"), types];
    assert.deepStrictEqual(planFixEdits(current, fixed), {
      replace: [{ index: 1, source: "var p = new P1(1);" }],
      insert: [{ index: 3, cell: types }],
    });
  });

  it("plans nothing when the cells are the same", () => {
    const current = [cell("setup", SETUP), cell("markdown", "# Title", "m1"), cell("code", "var a = 2;", "c1"), cell("types", "record R;")];
    const fixed = [part("setup", SETUP), part("markdown", "# Title", "m1"), part("code", "var a = 2;", "c1"), part("types", "record R;")];
    assert.deepStrictEqual(planFixEdits(current, fixed), { replace: [], insert: [] });
  });

  it("leaves a cell alone when the fixed list doesn't have its id", () => {
    const current = [cell("setup", SETUP), cell("code", "var a = 2;", "c1"), cell("code", C2, "c2")];
    const fixed = [part("setup", `${SETUP}\nusing System.Text;`), part("code", C2_FIXED, "c2")];
    assert.deepStrictEqual(planFixEdits(current, fixed), {
      replace: [
        { index: 0, source: `${SETUP}\nusing System.Text;` },
        { index: 2, source: C2_FIXED },
      ],
      insert: [],
    });
  });

  it("puts a new code cell after the cell before it in the fixed list", () => {
    const current = [cell("setup", SETUP), cell("code", "var a = 2;", "c1"), cell("code", "var b = 3;", "c2")];
    const added = part("code", "var n = 1;", "n1");
    const fixed = [part("setup", SETUP), part("code", "var a = 2;", "c1"), added, part("code", "var b = 3;", "c2")];
    assert.deepStrictEqual(planFixEdits(current, fixed), { replace: [], insert: [{ index: 2, cell: added }] });
  });
});
