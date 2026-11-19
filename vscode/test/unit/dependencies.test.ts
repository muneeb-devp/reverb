import * as assert from "node:assert";
import { dependenciesOf, dependencyItems } from "../../src/dependencies";

const label = (id: string) => ({ a: "load", b: "cell 2", c: "chart", d: "summary" })[id] ?? id;

describe("dependenciesOf", () => {
  it("has nothing for a cell without edges", () => {
    assert.deepStrictEqual(dependenciesOf("a", []), { uses: [], usedBy: [] });
  });

  it("reads an edge as from uses via from to", () => {
    const edges = [{ from: "b", to: "a", via: ["x", "y"] }];
    assert.deepStrictEqual(dependenciesOf("b", edges), { uses: [{ cell: "a", names: ["x", "y"] }], usedBy: [] });
    assert.deepStrictEqual(dependenciesOf("a", edges), { uses: [], usedBy: ["b"] });
  });

  it("ignores the types cell", () => {
    assert.deepStrictEqual(dependenciesOf("b", [{ from: "b", to: "types", via: ["Point"] }]), { uses: [], usedBy: [] });
  });
});

describe("dependencyItems", () => {
  it("gives no items without dependencies", () => {
    assert.deepStrictEqual(dependencyItems({ uses: [], usedBy: [] }, label), []);
  });

  it("says 1 cell in the singular and N cells in the plural", () => {
    const one = dependencyItems({ uses: [{ cell: "a", names: ["x"] }], usedBy: ["c"] }, label);
    assert.deepStrictEqual(one.map((i) => i.text), ["uses 1 cell", "used by 1 cell"]);
    const many = dependencyItems({ uses: [{ cell: "a", names: ["x"] }, { cell: "b", names: ["y"] }], usedBy: ["c", "d"] }, label);
    assert.deepStrictEqual(many.map((i) => i.text), ["uses 2 cells", "used by 2 cells"]);
  });

  it("lists the names and the cells they come from in the tooltips", () => {
    const items = dependencyItems({ uses: [{ cell: "a", names: ["orders", "threshold"] }], usedBy: ["c", "d"] }, label);
    assert.strictEqual(items[0].tooltip, "Uses orders, threshold from load");
    assert.strictEqual(items[1].tooltip, "Used by: chart, summary");
  });

  it("joins several sources with a semicolon", () => {
    const items = dependencyItems({ uses: [{ cell: "a", names: ["x"] }, { cell: "b", names: ["y"] }], usedBy: [] }, label);
    assert.strictEqual(items[0].tooltip, "Uses x from load; y from cell 2");
  });

  it("gives only the item that applies", () => {
    assert.deepStrictEqual(dependencyItems({ uses: [], usedBy: ["c"] }, label).map((i) => i.text), ["used by 1 cell"]);
    assert.deepStrictEqual(dependencyItems({ uses: [{ cell: "a", names: ["x"] }], usedBy: [] }, label).map((i) => i.text), ["uses 1 cell"]);
  });
});
