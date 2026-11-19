import * as assert from "node:assert";
import type { CellAttributeData } from "../../src/protocol";
import { hasFlag, nameOf, withFlag, withName } from "../../src/cellAttrs";

const other: CellAttributeData[] = [{ key: "x", value: "1" }, { key: "y" }];

describe("cellAttrs", () => {
  it("hasFlag finds a flag whatever its position", () => {
    assert.strictEqual(hasFlag([...other, { key: "disabled" }], "disabled"), true);
    assert.strictEqual(hasFlag(other, "disabled"), false);
    assert.strictEqual(hasFlag(undefined, "disabled"), false);
  });

  it("withFlag appends a missing flag and leaves the rest alone", () => {
    assert.deepStrictEqual(withFlag(other, "hide_code", true), [...other, { key: "hide_code" }]);
  });

  it("withFlag leaves a flag that is already on where it is", () => {
    const attrs = [{ key: "disabled" }, ...other];
    assert.deepStrictEqual(withFlag(attrs, "disabled", true), attrs);
  });

  it("withFlag removes a flag and keeps the order of the others", () => {
    assert.deepStrictEqual(withFlag([{ key: "x", value: "1" }, { key: "disabled" }, { key: "y" }], "disabled", false), other);
    assert.deepStrictEqual(withFlag(other, "disabled", false), other);
  });

  it("toggling a flag twice gives the original list", () => {
    const on = withFlag(other, "disabled", true);
    assert.deepStrictEqual(withFlag(on, "disabled", false), other);
  });

  it("does not change its input", () => {
    const attrs = [...other];
    withFlag(attrs, "disabled", true);
    withName(attrs, "load");
    assert.deepStrictEqual(attrs, other);
  });

  it("nameOf reads the name, or undefined when there is none or it is empty", () => {
    assert.strictEqual(nameOf([...other, { key: "name", value: "load" }]), "load");
    assert.strictEqual(nameOf(other), undefined);
    assert.strictEqual(nameOf([{ key: "name" }]), undefined);
    assert.strictEqual(nameOf([{ key: "name", value: "" }]), undefined);
    assert.strictEqual(nameOf(undefined), undefined);
  });

  it("withName adds a name at the end", () => {
    assert.deepStrictEqual(withName(other, "load"), [...other, { key: "name", value: "load" }]);
  });

  it("withName replaces a name where it is", () => {
    const attrs = [{ key: "name", value: "a" }, ...other];
    assert.deepStrictEqual(withName(attrs, "b"), [{ key: "name", value: "b" }, ...other]);
  });

  it("withName removes the name for undefined or an empty name", () => {
    const attrs = [{ key: "x", value: "1" }, { key: "name", value: "a" }, { key: "y" }];
    assert.deepStrictEqual(withName(attrs, undefined), other);
    assert.deepStrictEqual(withName(attrs, ""), other);
    assert.deepStrictEqual(withName(other, undefined), other);
  });
});
